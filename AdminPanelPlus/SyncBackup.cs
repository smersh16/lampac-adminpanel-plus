using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace AdminPanelPlus;

// Копии баз Sync (закладки/история) и TimeCode (позиции просмотра).
// Копия делается командой SQLite VACUUM INTO: она согласована и безопасна при работающем сервере.
// Исходные базы только читаются. Восстановление намеренно не автоматизировано (подмена БД под работающим сервером).
public static class SyncBackup
{
    public const string Dir = "database/backup/sync";
    public const int AutoKeep = 5;
    public const int ManualKeep = 10;
    static readonly TimeSpan AutoEvery = TimeSpan.FromHours(24);
    static readonly string[] Dbs = { "Sync", "TimeCode" };
    static readonly string SettingsFile = Path.Combine(Dir, "auto.json");

    public static readonly Regex NameRx = new(@"^(Sync|TimeCode)-\d{8}-\d{6}-(auto|manual)\.sql$", RegexOptions.Compiled);

    static readonly object gate = new();
    static Timer timer;

    public static void Start()
    {
        Stop();
        // проверка раз в 30 минут; первая через 2 минуты после старта, чтобы не мешать запуску
        timer = new Timer(_ => Tick(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));
    }

    public static void Stop()
    {
        timer?.Dispose();
        timer = null;
    }

    public static bool AutoEnabled()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return (bool?)JObject.Parse(File.ReadAllText(SettingsFile))["enabled"] == true;
        }
        catch { }
        return false;
    }

    public static void SetAuto(bool enabled)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(SettingsFile, new JObject { ["enabled"] = enabled }.ToString(Newtonsoft.Json.Formatting.None));
    }

    static void Tick()
    {
        try
        {
            if (!AutoEnabled())
                return;

            var last = Files().Where(f => f.Name.EndsWith("-auto.sql", StringComparison.Ordinal))
                .Select(f => f.LastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();

            if (DateTime.UtcNow - last >= AutoEvery)
                Create("auto", out _);
        }
        catch { }
    }

    public static List<FileInfo> Files()
    {
        if (!Directory.Exists(Dir))
            return new List<FileInfo>();

        return Directory.GetFiles(Dir).Select(p => new FileInfo(p))
            .Where(fi => NameRx.IsMatch(fi.Name))
            .OrderByDescending(fi => fi.Name, StringComparer.Ordinal).ToList();
    }

    public static IEnumerable<(string name, bool exists, long size)> Sources()
    {
        foreach (var db in Dbs)
        {
            var f = new FileInfo($"database/{db}.sql");
            yield return (db, f.Exists, f.Exists ? f.Length : 0);
        }
    }

    // kind: auto | manual. Возвращает имена созданных копий; error — текст ошибки (часть баз могла скопироваться).
    public static string[] Create(string kind, out string error)
    {
        error = null;
        var made = new List<string>();

        lock (gate)
        {
            Directory.CreateDirectory(Dir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

            foreach (var db in Dbs)
            {
                var src = $"database/{db}.sql";
                if (!File.Exists(src))
                    continue;

                var name = $"{db}-{stamp}-{kind}.sql";
                var dst = Path.Combine(Dir, name);
                try
                {
                    if (File.Exists(dst))
                        File.Delete(dst);

                    var cs = new SqliteConnectionStringBuilder { DataSource = src, Pooling = false }.ToString();
                    using (var con = new SqliteConnection(cs))
                    {
                        con.Open();
                        using var cmd = con.CreateCommand();
                        cmd.CommandText = "VACUUM INTO '" + dst.Replace("'", "''") + "'";
                        cmd.ExecuteNonQuery();
                    }
                    made.Add(name);
                }
                catch (Exception ex)
                {
                    error = db + ": " + ex.Message;
                    try { if (File.Exists(dst)) File.Delete(dst); } catch { }
                }
            }

            Rotate();
        }

        return made.ToArray();
    }

    static void Rotate()
    {
        foreach (var db in Dbs)
        foreach (var (kind, keep) in new[] { ("auto", AutoKeep), ("manual", ManualKeep) })
        {
            var old = Files().Where(f => f.Name.StartsWith(db + "-", StringComparison.Ordinal)
                                         && f.Name.EndsWith("-" + kind + ".sql", StringComparison.Ordinal))
                .Skip(keep);
            foreach (var f in old)
            {
                try { f.Delete(); } catch { }
            }
        }
    }
}
