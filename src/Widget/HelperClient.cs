using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ControllerToggleWidget
{
    internal enum ConnectResult
    {
        Connected,
        HelperNotRunning,
        AccessDenied,
        Busy,
        Failed
    }

    /// <summary>
    /// Talks to the ControllerToggle service over \\.\pipe\ControllerToggle.
    ///
    /// UWP's NamedPipeClientStream can't open a pipe owned by another (elevated) process, so -
    /// like GoTweaks' widget - we open it with CreateFileW and wrap the handle in a FileStream.
    /// The service's pipe ACL grants ALL APPLICATION PACKAGES, which includes this widget.
    /// </summary>
    internal sealed class HelperClient
    {
        private const string PipePath = @"\\.\pipe\ControllerToggle";

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_PIPE_BUSY = 231;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WaitNamedPipeW(string lpNamedPipeName, uint nTimeOut);

        private static readonly List<HelperClient> Instances = new List<HelperClient>();

        private readonly object _gate = new object();
        private readonly object _writeGate = new object();
        private FileStream _stream;
        private int _generation;

        public HelperClient()
        {
            lock (Instances)
            {
                Instances.Add(this);
            }
        }

        /// <summary>Closes every connection (the app is being suspended).</summary>
        public static void DisconnectAll()
        {
            HelperClient[] all;
            lock (Instances)
            {
                all = Instances.ToArray();
            }
            foreach (HelperClient client in all)
            {
                client.Disconnect();
            }
        }

        /// <summary>Disconnects and forgets this client (its widget window closed).</summary>
        public void Release()
        {
            Disconnect();
            lock (Instances)
            {
                Instances.Remove(this);
            }
        }

        /// <summary>Raised on a background thread for every line the helper sends.</summary>
        public event Action<string> LineReceived;

        /// <summary>Raised on a background thread when the connection drops.</summary>
        public event Action Disconnected;

        public int LastError { get; private set; }

        public bool IsConnected
        {
            get
            {
                lock (_gate)
                {
                    return _stream != null;
                }
            }
        }

        public Task<ConnectResult> ConnectAsync()
        {
            return Task.Run(() => Connect());
        }

        private ConnectResult Connect()
        {
            lock (_gate)
            {
                if (_stream != null)
                {
                    return ConnectResult.Connected;
                }
            }

            SafeFileHandle handle = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                handle = CreateFileW(PipePath, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (!handle.IsInvalid)
                {
                    break;
                }

                int error = Marshal.GetLastWin32Error();
                LastError = error;
                handle.Dispose();
                handle = null;

                if (error == ERROR_PIPE_BUSY)
                {
                    WaitNamedPipeW(PipePath, 1000);
                    continue;
                }
                if (error == ERROR_FILE_NOT_FOUND)
                {
                    return ConnectResult.HelperNotRunning;
                }
                if (error == ERROR_ACCESS_DENIED)
                {
                    return ConnectResult.AccessDenied;
                }
                return ConnectResult.Failed;
            }

            if (handle == null)
            {
                return ConnectResult.Busy;
            }

            FileStream stream;
            try
            {
                stream = new FileStream(handle, FileAccess.ReadWrite, 4096, true);
            }
            catch (Exception)
            {
                handle.Dispose();
                return ConnectResult.Failed;
            }

            int generation;
            lock (_gate)
            {
                _stream = stream;
                generation = ++_generation;
            }

            Task.Run(() => ReadLoop(stream, generation));
            return ConnectResult.Connected;
        }

        private async Task ReadLoop(FileStream stream, int generation)
        {
            try
            {
                using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true))
                {
                    while (true)
                    {
                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null)
                        {
                            break;
                        }
                        if (line.Length > 0)
                        {
                            Action<string> handler = LineReceived;
                            if (handler != null)
                            {
                                handler(line);
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Broken pipe / helper stopped: handled below.
            }
            finally
            {
                Drop(generation);
            }
        }

        /// <summary>Sends one command line. Returns false if not connected.</summary>
        public bool Send(string command)
        {
            FileStream stream;
            int generation;
            lock (_gate)
            {
                stream = _stream;
                generation = _generation;
            }
            if (stream == null)
            {
                return false;
            }

            byte[] data = Encoding.UTF8.GetBytes(command + "\n");
            Task.Run(() =>
            {
                try
                {
                    lock (_writeGate)
                    {
                        stream.Write(data, 0, data.Length);
                        stream.Flush();
                    }
                }
                catch (Exception)
                {
                    Drop(generation);
                }
            });
            return true;
        }

        public void Disconnect()
        {
            int generation;
            lock (_gate)
            {
                generation = _generation;
            }
            Drop(generation);
        }

        private void Drop(int generation)
        {
            FileStream stream;
            lock (_gate)
            {
                if (generation != _generation || _stream == null)
                {
                    return;
                }
                stream = _stream;
                _stream = null;
            }

            try
            {
                stream.Dispose();
            }
            catch (Exception)
            {
            }

            Action handler = Disconnected;
            if (handler != null)
            {
                handler();
            }
        }
    }
}
