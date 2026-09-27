// Small helpers around the JSON serializer that ships with the .NET Framework. It reads objects as
// Dictionary<string, object>, arrays as object[], and numbers as int, long or decimal.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

public static class Json {
  static readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

  public static object Read(string text) { return serializer.DeserializeObject(text); }
  public static string Write(object value) { return serializer.Serialize(value); }

  // Notepad and PowerShell 5 may save a file with a byte order mark, and PowerShell 5's ">" writes UTF-16.
  public static object ReadFile(string path) {
    var bytes = File.ReadAllBytes(path);
    var text = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
    return Read(text.TrimStart((char)0xFEFF));
  }

  public static object Get(Dictionary<string, object> map, string key) {
    object value;
    return map != null && map.TryGetValue(key, out value) ? value : null;
  }
  public static Dictionary<string, object> Obj(object value) { return value as Dictionary<string, object>; }
  public static object[] Arr(object value) { return value as object[]; }
  public static string Str(object value) { return value as string ?? ""; }
  public static bool Bool(object value) { return value is bool && (bool)value; }
  public static bool IsNumber(object value) { return value is int || value is long || value is decimal || value is double || value is float; }
  public static double Num(object value) { return IsNumber(value) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : 0; }
  public static int Int(object value) { return IsNumber(value) ? (int)Convert.ToDouble(value, CultureInfo.InvariantCulture) : 0; }

  // Equality as JavaScript's === sees it for the values Lanes compares: numbers by value, the rest by Equals.
  public static bool Same(object a, object b) {
    if (IsNumber(a) && IsNumber(b)) return Num(a) == Num(b);
    return Equals(a, b);
  }

  // One key per line, like JSON.stringify(value, null, 2) for a flat object: settings.json stays easy to edit.
  public static string WriteFlat(IDictionary<string, object> map) {
    var text = new StringBuilder("{\n");
    var i = 0;
    foreach (var pair in map) {
      text.Append("  ").Append(Write(pair.Key)).Append(": ").Append(Write(pair.Value)).Append(++i < map.Count ? ",\n" : "\n");
    }
    return text.Append("}\n").ToString();
  }
}
