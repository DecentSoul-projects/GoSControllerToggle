using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ControllerToggle.Helper
{
    /// <summary>Minimal JSON object writer (the helper only ever writes flat objects).</summary>
    internal sealed class JsonWriter
    {
        private readonly StringBuilder _sb = new StringBuilder("{");
        private bool _first = true;

        public JsonWriter Add(string name, string value)
        {
            Key(name);
            if (value == null)
            {
                _sb.Append("null");
            }
            else
            {
                AppendString(_sb, value);
            }
            return this;
        }

        public JsonWriter Add(string name, bool value)
        {
            Key(name);
            _sb.Append(value ? "true" : "false");
            return this;
        }

        public JsonWriter Add(string name, int value)
        {
            Key(name);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Add(string name, IEnumerable<string> values)
        {
            Key(name);
            _sb.Append('[');
            bool first = true;
            foreach (string value in values)
            {
                if (!first)
                {
                    _sb.Append(',');
                }
                first = false;
                AppendString(_sb, value ?? "");
            }
            _sb.Append(']');
            return this;
        }

        public override string ToString()
        {
            return _sb.ToString() + "}";
        }

        private void Key(string name)
        {
            if (!_first)
            {
                _sb.Append(',');
            }
            _first = false;
            AppendString(_sb, name);
            _sb.Append(':');
        }

        private static void AppendString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
