using System.Collections.Generic;
using System.Text;

namespace Matrix.RustSdk.Generators;

/// <summary>
/// Writes indented lines, the generated code is laid out like csharpier output so the snapshots are readable.
/// </summary>
internal sealed class CodeWriter
{
    private const string IndentUnit = "    ";

    private readonly StringBuilder _builder = new();
    private int _depth;

    public void Line(string text = "")
    {
        if (text.Length > 0)
        {
            for (int i = 0; i < _depth; i++)
            {
                _builder.Append(IndentUnit);
            }
            _builder.Append(text);
        }
        _builder.Append('\n');
    }

    /// <summary>
    /// Writes <paramref name="lines"/> one level deeper than the current line.
    /// </summary>
    public void Indented(IEnumerable<string> lines)
    {
        Indent();
        foreach (string line in lines)
        {
            Line(line);
        }
        Outdent();
    }

    /// <summary>
    /// Appends <paramref name="text"/> to the last line written.
    /// </summary>
    public void AppendToLastLine(string text) => _builder.Insert(_builder.Length - 1, text);

    public void Indent() => _depth++;

    public void Outdent() => _depth--;

    public void Open()
    {
        Line("{");
        Indent();
    }

    public void Close()
    {
        Outdent();
        Line("}");
    }

    public override string ToString() => _builder.ToString();
}
