// ============================================================
//  Csv.cs
//  A small, correct, streaming CSV reader for the Graph usage
//  reports. Handles RFC-4180 quoting: quoted fields, doubled
//  quotes ("") as a literal quote, and commas/newlines inside
//  quotes. Reads from a TextReader so the BOM is already stripped
//  by the StreamReader. Allocates one row at a time.
// ============================================================
using System.Text;

namespace Prism.Connectors;

internal static class Csv
{
    public static IEnumerable<string[]> ReadRows(TextReader reader)
    {
        var field = new StringBuilder();
        var row = new List<string>();
        bool inQuotes = false;
        bool sawAny = false;
        int ci;

        while ((ci = reader.Read()) != -1)
        {
            char c = (char)ci;
            sawAny = true;

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); } // escaped quote
                    else inQuotes = false;                                           // end of quoted field
                }
                else field.Append(c);                                                // commas/newlines kept verbatim
            }
            else
            {
                switch (c)
                {
                    case '"': inQuotes = true; break;
                    case ',': row.Add(field.ToString()); field.Clear(); break;
                    case '\r': break;                                                // swallow; \n ends the row
                    case '\n':
                        row.Add(field.ToString()); field.Clear();
                        yield return row.ToArray();
                        row.Clear();
                        break;
                    default: field.Append(c); break;
                }
            }
        }

        // Final field/row when the file doesn't end in a newline.
        if (sawAny && (field.Length > 0 || row.Count > 0))
        {
            row.Add(field.ToString());
            yield return row.ToArray();
        }
    }
}
