using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ControllerToggle.Helper.Ipc
{
    /// <summary>
    /// Line-based named pipe server (\\.\pipe\ControllerToggle) for the Game Bar widget.
    ///
    /// Protocol: the client sends one command per line ("get", "enable", "disable",
    /// "auto on", "auto off"); the server answers each with one JSON state line and also
    /// pushes a state line to every client whenever something changes.
    ///
    /// The widget runs in an AppContainer (UWP sandbox, Low integrity), the service runs as
    /// SYSTEM. Same approach GoTweaks uses between its widget and elevated helper: the pipe's
    /// DACL grants ALL APPLICATION PACKAGES, plus a Low mandatory label so the sandboxed
    /// client may write to it.
    /// </summary>
    internal sealed class PipeServer : IDisposable
    {
        public const string PipeName = "ControllerToggle";

        // SYSTEM, Administrators: full control. Authenticated users, ALL APPLICATION PACKAGES
        // (S-1-15-2-1) and ALL RESTRICTED APPLICATION PACKAGES (S-1-15-2-2): read/write.
        // Low integrity, no-write-up label so Low-IL (AppContainer) clients can write.
        private const string Sddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;AU)(A;;GRGW;;;S-1-15-2-1)(A;;GRGW;;;S-1-15-2-2)S:(ML;;NW;;;LW)";

        private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint PIPE_TYPE_BYTE = 0x00000000;
        private const uint PIPE_READMODE_BYTE = 0x00000000;
        private const uint PIPE_WAIT = 0x00000000;
        private const uint PIPE_REJECT_REMOTE_CLIENTS = 0x00000008;
        private const uint SDDL_REVISION_1 = 1;
        private const uint MaxInstances = 16;
        private const int ERROR_PIPE_BUSY = 231;
        private const int MaxLineBytes = 256;
        private const int WriteTimeoutMs = 2000;
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateNamedPipeW(string lpName, uint dwOpenMode, uint dwPipeMode, uint nMaxInstances, uint nOutBufferSize, uint nInBufferSize, uint nDefaultTimeOut, ref SECURITY_ATTRIBUTES lpSecurityAttributes);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string StringSecurityDescriptor, uint StringSDRevision, out IntPtr SecurityDescriptor, IntPtr SecurityDescriptorSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private sealed class Client
        {
            public int Id;
            public NamedPipeServerStream Stream;
            public readonly object WriteLock = new object();
            public DateTime LastActivityUtc;
        }

        private readonly Func<string, string> _handler;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly List<Client> _clients = new List<Client>();
        private readonly object _clientsGate = new object();
        private Thread _acceptThread;
        private Timer _janitor;
        private int _nextClientId;

        public PipeServer(Func<string, string> handler)
        {
            _handler = handler;
        }

        public void Start()
        {
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ControllerToggle.PipeAccept" };
            _acceptThread.Start();
            _janitor = new Timer(CloseIdleClients, null, 30000, 30000);
            Log.Info("Pipe server listening on \\\\.\\pipe\\" + PipeName);
        }

        private void AcceptLoop()
        {
            while (!_stop.WaitOne(0))
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = CreateInstance();
                    IAsyncResult pending = server.BeginWaitForConnection(null, null);
                    int which = WaitHandle.WaitAny(new WaitHandle[] { pending.AsyncWaitHandle, _stop });
                    if (which == 1)
                    {
                        server.Dispose();
                        return;
                    }
                    server.EndWaitForConnection(pending);

                    var client = new Client
                    {
                        Id = Interlocked.Increment(ref _nextClientId),
                        Stream = server,
                        LastActivityUtc = DateTime.UtcNow
                    };
                    server = null;
                    lock (_clientsGate)
                    {
                        _clients.Add(client);
                    }
                    var thread = new Thread(() => ClientLoop(client)) { IsBackground = true, Name = "ControllerToggle.PipeClient" };
                    thread.Start();
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_PIPE_BUSY)
                {
                    // Every instance is taken - most likely by suspended widgets that never closed
                    // their end. Drop the oldest client and try again.
                    Log.Warn("All pipe instances busy; dropping the oldest client.");
                    DropOldestClient();
                    if (_stop.WaitOne(500))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    if (_stop.WaitOne(0))
                    {
                        return;
                    }
                    Log.Warn("Pipe accept failed: " + ex.Message);
                    if (_stop.WaitOne(1000))
                    {
                        return;
                    }
                }
                finally
                {
                    if (server != null)
                    {
                        try
                        {
                            server.Dispose();
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private static NamedPipeServerStream CreateInstance()
        {
            IntPtr descriptor;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, SDDL_REVISION_1, out descriptor, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var attributes = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)),
                    lpSecurityDescriptor = descriptor,
                    bInheritHandle = 0
                };

                IntPtr handle = CreateNamedPipeW(
                    @"\\.\pipe\" + PipeName,
                    PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                    PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                    MaxInstances,
                    4096,
                    4096,
                    0,
                    ref attributes);

                if (handle == InvalidHandleValue || handle == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                return new NamedPipeServerStream(PipeDirection.InOut, true, false, new SafePipeHandle(handle, true));
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        private void ClientLoop(Client client)
        {
            var pending = new List<byte>(64);
            var chunk = new byte[512];
            try
            {
                while (!_stop.WaitOne(0))
                {
                    int read = client.Stream.Read(chunk, 0, chunk.Length);
                    if (read <= 0)
                    {
                        break;
                    }
                    client.LastActivityUtc = DateTime.UtcNow;

                    for (int i = 0; i < read; i++)
                    {
                        byte b = chunk[i];
                        if (b == (byte)'\n')
                        {
                            string line = Utf8.GetString(pending.ToArray()).Replace("\uFEFF", "").Trim();
                            pending.Clear();
                            if (line.Length == 0)
                            {
                                continue;
                            }
                            string reply = _handler(line);
                            if (reply != null && !Send(client, reply))
                            {
                                return;
                            }
                        }
                        else if (b != (byte)'\r')
                        {
                            pending.Add(b);
                            if (pending.Count > MaxLineBytes)
                            {
                                Log.Warn("Pipe client " + client.Id + " sent an oversized line; disconnecting.");
                                return;
                            }
                        }
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Warn("Pipe client " + client.Id + " error: " + ex.Message);
            }
            finally
            {
                Remove(client);
            }
        }

        /// <summary>Writes one line with a timeout; a client that stops reading is dropped.</summary>
        private bool Send(Client client, string line)
        {
            byte[] data = Utf8.GetBytes(line + "\n");
            try
            {
                lock (client.WriteLock)
                {
                    IAsyncResult write = client.Stream.BeginWrite(data, 0, data.Length, null, null);
                    if (!write.AsyncWaitHandle.WaitOne(WriteTimeoutMs))
                    {
                        throw new TimeoutException("the client isn't reading");
                    }
                    client.Stream.EndWrite(write);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("Dropping pipe client " + client.Id + ": " + ex.Message);
                Remove(client);
                return false;
            }
        }

        public void Broadcast(string line)
        {
            List<Client> snapshot;
            lock (_clientsGate)
            {
                snapshot = new List<Client>(_clients);
            }
            foreach (Client client in snapshot)
            {
                Send(client, line);
            }
        }

        private void CloseIdleClients(object state)
        {
            List<Client> idle = new List<Client>();
            lock (_clientsGate)
            {
                foreach (Client client in _clients)
                {
                    if (DateTime.UtcNow - client.LastActivityUtc > IdleTimeout)
                    {
                        idle.Add(client);
                    }
                }
            }
            foreach (Client client in idle)
            {
                Remove(client);
            }
        }

        private void DropOldestClient()
        {
            Client oldest = null;
            lock (_clientsGate)
            {
                foreach (Client client in _clients)
                {
                    if (oldest == null || client.LastActivityUtc < oldest.LastActivityUtc)
                    {
                        oldest = client;
                    }
                }
            }
            if (oldest != null)
            {
                Remove(oldest);
            }
        }

        private void Remove(Client client)
        {
            bool removed;
            lock (_clientsGate)
            {
                removed = _clients.Remove(client);
            }
            if (removed)
            {
                try
                {
                    client.Stream.Dispose();
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            _stop.Set();
            if (_janitor != null)
            {
                _janitor.Dispose();
            }

            List<Client> snapshot;
            lock (_clientsGate)
            {
                snapshot = new List<Client>(_clients);
            }
            foreach (Client client in snapshot)
            {
                Remove(client);
            }

            if (_acceptThread != null)
            {
                _acceptThread.Join(2000);
            }
        }
    }

    /// <summary>Client used by the command line (--ping, --enable, ...) to talk to the running service.</summary>
    internal static class PipeClient
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>Sends one command and returns the first line of the reply, or null if the service isn't reachable.</summary>
        public static string Request(string command, int timeoutMs)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", PipeServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(Math.Min(timeoutMs, 2000));
                    byte[] data = Utf8.GetBytes(command + "\n");
                    pipe.Write(data, 0, data.Length);
                    pipe.Flush();

                    var line = new List<byte>(512);
                    var chunk = new byte[1024];
                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    while (DateTime.UtcNow < deadline)
                    {
                        IAsyncResult read = pipe.BeginRead(chunk, 0, chunk.Length, null, null);
                        int remaining = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                        if (!read.AsyncWaitHandle.WaitOne(remaining))
                        {
                            return null;
                        }
                        int count = pipe.EndRead(read);
                        if (count <= 0)
                        {
                            return null;
                        }
                        for (int i = 0; i < count; i++)
                        {
                            if (chunk[i] == (byte)'\n')
                            {
                                return Utf8.GetString(line.ToArray()).Trim();
                            }
                            line.Add(chunk[i]);
                        }
                    }
                }
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            return null;
        }
    }
}
