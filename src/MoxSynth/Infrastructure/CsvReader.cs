using System.Text;

namespace MoxSynth.Infrastructure;

/// <summary>
/// Small RFC 4180 CSV reader: quoted fields, doubled quotes, line breaks inside quotes, BOM.
/// The delimiter (comma, semicolon or tab) is detected from the header line.
/// </summary>
public static class CsvReader
{
    public static List<string[]> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var delimiter = DetectDelimiter(text);

        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            if (c == '"') inQuotes = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
                row.Clear();
            }
            else field.Append(c);
        }

        row.Add(field.ToString());
        if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
        return rows;
    }

    private static char DetectDelimiter(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var header = end < 0 ? text : text[..end];
        var candidates = new[] { ',', ';', '\t' };
        return candidates.OrderByDescending(d => header.Count(ch => ch == d)).First();
    }
}
