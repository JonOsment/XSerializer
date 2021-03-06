using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XSerializer
{
    internal class JsonReader : IDisposable
    {
        private readonly TextReader _primaryReader;
        private readonly IJsonSerializeOperationInfo _info;

        private StringReader _decryptedReader;
        private TextReader _currentReader;
        private bool _decryptReads;

        public JsonReader(TextReader reader, IJsonSerializeOperationInfo info)
        {
            _primaryReader = reader;
            _currentReader = _primaryReader;
            _info = info;
        }

        public JsonNodeType NodeType { get; private set; }
        public object Value { get; private set; }

        public bool DecryptReads
        {
            get { return _decryptReads; }
            set
            {
                if (value == _decryptReads)
                {
                    return;
                }

                _decryptReads = value;

                if (_decryptReads)
                {
                    if (NodeType == JsonNodeType.Null)
                    {
                        return;
                    }

                    if (NodeType != JsonNodeType.String)
                    {
                        throw new XSerializerException("Cannot decrypt non-string value.");
                    }

                    _decryptedReader = new StringReader(_info.EncryptionMechanism.Decrypt((string)Value, _info.EncryptKey, _info.SerializationState));
                    _currentReader = _decryptedReader;
                    Read();
                }
                else
                {
                    if (NodeType == JsonNodeType.Null)
                    {
                        return;
                    }

                    if (_decryptedReader.Peek() != -1)
                    {
                        throw new InvalidOperationException("Attempted to set DecryptReads to false before the encrypted stream has been consumed.");
                    }

                    _decryptedReader = null;
                    _currentReader = _primaryReader;
                }
            }
        }

        public void Dispose()
        {
            // TODO: Something?
        }

        /// <summary>
        /// Reads the next non-whitespace node from the stream.
        /// </summary>
        /// <returns>true if the next node was read successfully; false if there are no more nodes to read.</returns>
        public bool ReadContent()
        {
            while (true)
            {
                if (!Read())
                {
                    return false;
                }

                if (NodeType != JsonNodeType.Whitespace)
                {
                    return true;
                }
            }
        }

        /// <summary>
        /// Read the properties of a json object. The resulting collection contains the name of each
        /// property. As the collection is enumerated, for each property name, the reader is positioned
        /// at the beginning of the property's value. The caller is expected to parse the value, calling
        /// <see cref="Read"/> or <see cref="ReadContent"/> one or more times, before continuing to
        /// enumerate the collection.
        /// </summary>
        /// <exception cref="XSerializerException">If the JSON object is malformed.</exception>
        public IEnumerable<string> ReadProperties()
        {
            if (NodeType != JsonNodeType.OpenObject)
            {
                throw new XSerializerException("Unexpected node type found while attempting to parse '{' character: " + NodeType + ".");
            }

            while (true)
            {
                if (!ReadContent())
                {
                    throw new XSerializerException("Unexpected end of input while attempting to parse property name.");
                }

                switch (NodeType)
                {
                    case JsonNodeType.CloseObject:
                        yield break;
                    case JsonNodeType.String:
                        break;
                    default:
                        throw new XSerializerException("Unexpected node type found while attempting to parse ':' character: " + NodeType + ".");
                }

                var name = (string)Value;

                if (!ReadContent())
                {
                    throw new XSerializerException("Unexpected end of input while attempting to parse ':' character.");
                }

                if (NodeType != JsonNodeType.NameValueSeparator)
                {
                    throw new XSerializerException("Unexpected node type found while attempting to parse ':' character: " + NodeType + ".");
                }

                yield return name;

                // The caller is expected to make one or more Read calls after receiving the yielded property name.

                if (!ReadContent())
                {
                    throw new XSerializerException("Unexpected end of input while attempting to parse ',' or '}' character.");
                }

                switch (NodeType)
                {
                    case JsonNodeType.CloseObject:
                        yield break;
                    case JsonNodeType.ItemSeparator:
                        break;
                    default:
                        throw new XSerializerException("Unexpected node type found while attempting to parse ',' or '}' character: " + NodeType + ".");
                }
            }
        }

        /// <summary>
        /// Read and discard the next content value. If the next content type is <see cref="JsonNodeType.OpenObject"/>
        /// or <see cref="JsonNodeType.OpenArray"/>, then the reader will continue to read and discard content until
        /// the matching <see cref="JsonNodeType.CloseObject"/> or <see cref="JsonNodeType.CloseArray"/> content type
        /// is found. For all other content types, no additional reads are made.
        /// </summary>
        public void Discard()
        {
            if (!ReadContent())
            {
                return;
            }

            switch (NodeType)
            {
                case JsonNodeType.OpenObject:
                    Consume(JsonNodeType.OpenObject, JsonNodeType.CloseObject);
                    break;
                case JsonNodeType.OpenArray:
                    Consume(JsonNodeType.OpenArray, JsonNodeType.CloseArray);
                    break;
            }
        }

        private void Consume(JsonNodeType openNodeType, JsonNodeType closeNodeType)
        {
            int nestLevel = 0;

            while (Read())
            {
                if (NodeType == closeNodeType)
                {
                    if (nestLevel == 0)
                    {
                        return;
                    }

                    nestLevel--;
                }
                else if (NodeType == openNodeType)
                {
                    nestLevel++;
                }
            }
        }

        /// <summary>
        /// Reads the next non-whitespace node type without changing the state of the reader. If the
        /// next node type is whitespace, then all leading whitespace is consumed and discarded. The
        /// next node type is then returned, again without changing the state of the reader.
        /// </summary>
        /// <returns>The next non-whitespace node type in the stream.</returns>
        public JsonNodeType PeekNextNodeType()
        {
            while (true)
            {
                var peek = _currentReader.Peek();

                switch (peek)
                {
                    case ' ':
                    case '\r':
                    case '\n':
                    case '\t':
                        ReadWhitespace((char)peek);
                        continue;
                    case -1:
                        return JsonNodeType.None;
                    case '"':
                        return JsonNodeType.String;
                    case '-':
                    case '.':
                    case '0':
                    case '1':
                    case '2':
                    case '3':
                    case '4':
                    case '5':
                    case '6':
                    case '7':
                    case '8':
                    case '9':
                        return JsonNodeType.Number;
                    case 't':
                    case 'f':
                        return JsonNodeType.Boolean;
                    case 'n':
                        return JsonNodeType.Null;
                    case '{':
                        return JsonNodeType.OpenObject;
                    case '}':
                        return JsonNodeType.CloseObject;
                    case ':':
                        return JsonNodeType.NameValueSeparator;
                    case ',':
                        return JsonNodeType.ItemSeparator;
                    case '[':
                        return JsonNodeType.OpenArray;
                    case ']':
                        return JsonNodeType.CloseArray;
                    default:
                        throw new XSerializerException("Unknown character: " + (char)peek);
                }
            }
        }

        /// <summary>
        /// Reads the next node from the stream.
        /// </summary>
        /// <returns>true if the next node was read successfully; false if there are no more nodes to read.</returns>
        public bool Read()
        {
            var read = _currentReader.Read();

            switch (read)
            {
                case -1:
                    Value = null;
                    NodeType = JsonNodeType.None;
                    return false;
                case '"':
                    Value = ReadString();
                    NodeType = JsonNodeType.String;
                    break;
                case '-':
                case '.':
                case '0':
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                    Value = ReadNumber((char)read);
                    NodeType = JsonNodeType.Number;
                    break;
                case 't':
                    ReadLiteral("true", 'r', 'u', 'e');
                    Value = true;
                    NodeType = JsonNodeType.Boolean;
                    break;
                case 'f':
                    ReadLiteral("false", 'a', 'l', 's', 'e');
                    Value = false;
                    NodeType = JsonNodeType.Boolean;
                    break;
                case 'n':
                    ReadLiteral("null", 'u', 'l', 'l');
                    Value = null;
                    NodeType = JsonNodeType.Null;
                    break;
                case '{':
                    Value = '{';
                    NodeType = JsonNodeType.OpenObject;
                    break;
                case '}':
                    Value = '}';
                    NodeType = JsonNodeType.CloseObject;
                    break;
                case ':':
                    Value = ':';
                    NodeType = JsonNodeType.NameValueSeparator;
                    break;
                case ',':
                    Value = ',';
                    NodeType = JsonNodeType.ItemSeparator;
                    break;
                case '[':
                    Value = '[';
                    NodeType = JsonNodeType.OpenArray;
                    break;
                case ']':
                    Value = ']';
                    NodeType = JsonNodeType.CloseArray;
                    break;
                case ' ':
                case '\r':
                case '\n':
                case '\t':
                    Value = ReadWhitespace((char)read);
                    NodeType = JsonNodeType.Whitespace;
                    break;
            }

            return true;
        }

        private void ReadLiteral(string value, params char[] literalMinusFirstChar)
        {
            for (int i = 0; i < literalMinusFirstChar.Length; i++)
            {
                var read = _currentReader.Read();

                if (read == -1)
                {
                    throw new XSerializerException(string.Format("Reached end of input before literal '{0}' was parsed.", value));
                }

                if (read != literalMinusFirstChar[i])
                {
                    throw new XSerializerException(string.Format("Invalid literal character '{0}' in literal '{1}.", (char)read, value));
                }
            }
        }

        private string ReadString()
        {
            var sb = new StringBuilder(38); // Large enough to read a DateTime or Guid.

            while (true)
            {
                var read = _currentReader.Read();

                switch (read)
                {
                    case '"':
                        return sb.ToString();
                    case '\\':
                        sb.Append(ReadEscapedChar());
                        break;
                    case -1:
                        throw new XSerializerException("Reached end of input before closing quote was found for string.");
                    default:
                        sb.Append((char)read);
                        break;
                }
            }
        }

        private char ReadEscapedChar()
        {
            var read = _currentReader.Read();

            switch (read)
            {
                case '"':
                case '\\':
                case '/':
                    return (char)read;
                case 'b':
                    return '\b';
                case 'f':
                    return '\f';
                case 'n':
                    return '\n';
                case 'r':
                    return '\r';
                case 't':
                    return '\t';
                case 'u':
                    throw new NotImplementedException("Hexadecimal unicode escape characters have not yet been implemented.");
                case -1:
                    throw new XSerializerException("Reached end of input before reading escaped character.");
                default:
                    throw new XSerializerException("Unknown escaped character: \\" + (char)read);
            }
        }

        private string ReadNumber(char c)
        {
            var sb = new StringBuilder();
            sb.Append(c);

            while (true)
            {
                var peek = _currentReader.Peek();

                switch (peek)
                {
                    case '+':
                    case '-':
                    case '.':
                    case 'e':
                    case 'E':
                    case '0':
                    case '1':
                    case '2':
                    case '3':
                    case '4':
                    case '5':
                    case '6':
                    case '7':
                    case '8':
                    case '9':
                        break;
                    default:
                        return sb.ToString();
                }

                sb.Append((char)_currentReader.Read());
            }
        }

        private string ReadWhitespace(char first)
        {
            var sb = new StringBuilder();
            sb.Append(first);

            while (IsWhitespace(_currentReader.Peek()))
            {
                sb.Append((char)_currentReader.Read());
            }

            return sb.ToString();
        }

        private static bool IsWhitespace(int c)
        {
            switch (c)
            {
                case ' ':
                case '\r':
                case '\n':
                case '\t':
                    return true;
                default:
                    return false;
            }
        }
    }
}