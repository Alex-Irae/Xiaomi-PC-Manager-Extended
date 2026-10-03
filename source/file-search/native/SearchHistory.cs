// Purpose: account-encrypted query history and per-query file-open preferences.
// Dependencies: .NET 8, existing AccountProtection. Output: data/history.dpapi.
// Check: native/bin/Release/net8.0-windows/AI Center.exe --check-history --data <new directory>.
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace LocalAICenter;

internal sealed class SearchHistory(string directory)
{
    readonly string path=Path.Combine(directory,"history.dpapi");
    internal sealed class Opened
    {
        public string Query {get;set;}="";
        public string Path {get;set;}="";
        public int Count {get;set;}
        public long Last {get;set;}
    }
    internal sealed class State
    {
        public string[] Queries {get;set;}=Array.Empty<string>();
        public List<Opened> Opens {get;set;}=new();
    }
    static string Clean(string query)=>string.Join(" ",query.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
    static string Key(string query)=>Clean(query).ToUpperInvariant();
    internal State Read()
    {
        if(!File.Exists(path))return new();
        using var document=JsonDocument.Parse(AccountProtection.Crypt(File.ReadAllBytes(path),true));
        return document.RootElement.ValueKind==JsonValueKind.Array?new State {Queries=document.RootElement.Deserialize<string[]>()!}:document.RootElement.Deserialize<State>()!;
    }
    void Write(State state)
    {
        var keep=state.Queries.Select(Key).ToHashSet();
        state.Opens=state.Opens.Where(item=>keep.Contains(item.Query)).ToList();
        string temporary=path+".pending";
        File.WriteAllBytes(temporary,AccountProtection.Crypt(JsonSerializer.SerializeToUtf8Bytes(state),false));
        File.Move(temporary,path,true);
    }
    static void Add(State state,string query)
    {
        query=Clean(query);if(query.Length is <1 or >256)throw new ArgumentException("Search history supports 1 to 256 characters");
        state.Queries=new[]{query}.Concat(state.Queries.Where(value=>Key(value)!=Key(query))).Take(20).ToArray();
    }
    internal State Remember(string query){var state=Read();Add(state,query);Write(state);return state;}
    internal State Save(string[] queries)
    {
        if(queries.Length>20||queries.Any(value=>string.IsNullOrWhiteSpace(value)||value.Length>256))throw new ArgumentException("Invalid search history");
        var state=Read();state.Queries=queries.Select(Clean).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();Write(state);return state;
    }
    internal State Remove(string query){var state=Read();state.Queries=state.Queries.Where(value=>Key(value)!=Key(query)).ToArray();Write(state);return state;}
    internal State Clear(){var state=new State();Write(state);return state;}
    internal void RecordOpen(string query,string file)
    {
        if(string.IsNullOrWhiteSpace(query)||Clean(query).Length>256)return;
        var state=Read();Add(state,query);string full=System.IO.Path.GetFullPath(file),key=Key(query);
        var item=state.Opens.FirstOrDefault(item=>item.Query==key&&string.Equals(item.Path,full,StringComparison.OrdinalIgnoreCase));
        if(item is null){item=new Opened {Query=key,Path=full};state.Opens.Add(item);}
        item.Count=Math.Min(1000000,item.Count+1);item.Last=DateTime.UtcNow.Ticks;
        // ponytail: bounded personal history, not an unbounded usage database.
        state.Opens=state.Opens.GroupBy(value=>value.Query).SelectMany(group=>group.OrderByDescending(value=>value.Count).ThenByDescending(value=>value.Last).Take(20)).ToList();
        Write(state);
    }
    internal string[] Preferred(string query)=>Read().Opens.Where(item=>item.Query==Key(query)).OrderByDescending(item=>item.Count).ThenByDescending(item=>item.Last).Select(item=>item.Path).ToArray();
    internal static object Check(string directory)
    {
        var history=new SearchHistory(directory);string a=System.IO.Path.Combine(directory,"first.txt"),b=System.IO.Path.Combine(directory,"second.txt");
        File.WriteAllBytes(history.path,AccountProtection.Crypt(Encoding.UTF8.GetBytes("[\"EFS\"]"),false));
        if(history.Read().Queries.Single()!="EFS")throw new Exception("Legacy query migration failed");
        history.RecordOpen("  efs ",a);history.RecordOpen("EFS",b);history.RecordOpen("efs",a);
        var reopened=new SearchHistory(directory);if(reopened.Preferred(" EFS ").First()!=a)throw new Exception("Open count priority failed");
        reopened.RecordOpen("efs",b);if(reopened.Preferred("efs").First()!=b)throw new Exception("Recent tie-break failed");
        if(Encoding.UTF8.GetString(File.ReadAllBytes(history.path)).Contains("first.txt"))throw new Exception("History leaked plaintext");
        reopened.Remove("EFS");if(reopened.Read().Queries.Length!=0||reopened.Preferred("efs").Length!=0)throw new Exception("Remove did not forget preferences");
        reopened.Remember("other");reopened.Clear();if(reopened.Read().Queries.Length!=0)throw new Exception("Clear failed");
        return new {passed=true,legacyMigration=true,accountEncrypted=true,mostOpenedFirst=true,recentTieBreak=true,persistsAfterRestart=true,removeForgetsChoices=true,clear=true};
    }
}

internal static class FileActions
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct OpenAsInfo { [MarshalAs(UnmanagedType.LPWStr)]public string File;[MarshalAs(UnmanagedType.LPWStr)]public string? Class;public uint Flags; }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern int SHOpenWithDialog(IntPtr parent,ref OpenAsInfo info);
    internal static bool OpenWith(string file)
    {
        if(Directory.Exists(file))throw new ArgumentException("Open with applies to files");
        var info=new OpenAsInfo {File=file,Flags=4}; // OAIF_EXEC: let Windows choose and launch the application.
        int result=SHOpenWithDialog(IntPtr.Zero,ref info);
        if(result==unchecked((int)0x800704C7))return false; // User canceled.
        Marshal.ThrowExceptionForHR(result);return result==0;
    }
}
