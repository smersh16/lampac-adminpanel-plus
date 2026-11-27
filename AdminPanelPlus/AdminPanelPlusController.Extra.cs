using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AdminPanelPlus;

public partial class AdminPanelPlusController
{
    #region sync backup
    [HttpGet]
    [Route("/adminpanel/api/sync-backup")]
    public ActionResult SyncBackupInfo()
    {
        var o = new JObject
        {
            ["auto"] = SyncBackup.AutoEnabled(),
            ["autoKeep"] = SyncBackup.AutoKeep,
            ["dir"] = SyncBackup.Dir,
            ["sources"] = new JArray(SyncBackup.Sources().Select(s => new JObject
            {
                ["name"] = s.name,
                ["exists"] = s.exists,
                ["size"] = s.size
            })),
            ["files"] = new JArray(SyncBackup.Files().Select(f => new JObject
            {
                ["name"] = f.Name,
                ["size"] = f.Length,
                ["utc"] = f.LastWriteTimeUtc.ToString("o")
            }))
        };

        return Content(o.ToString(Formatting.None), "application/json; charset=utf-8");
    }

    [HttpPost]
    [Route("/adminpanel/api/sync-backup/create")]
    public ActionResult SyncBackupCreate()
    {
        var made = SyncBackup.Create("manual", out var error);
        if (made.Length == 0)
            return AdminJsonError(500, "backup failed", error ?? "no databases found");

        var o = new JObject { ["ok"] = true, ["made"] = new JArray(made) };
        if (error != null)
            o["warning"] = error;

        return Content(o.ToString(Formatting.None), "application/json; charset=utf-8");
    }

    // Тело: {"enabled":true}
    [HttpPost]
    [Route("/adminpanel/api/sync-backup/auto")]
    public async Task<IActionResult> SyncBackupAuto()
    {
        try
        {
            var enabled = (bool)JObject.Parse(await ReadBodyAsync().ConfigureAwait(false))["enabled"];
            SyncBackup.SetAuto(enabled);
            return AdminJsonOk();
        }
        catch (Exception ex) when (ex is JsonException || ex is NullReferenceException || ex is InvalidCastException)
        {
            return AdminJsonError(400, "invalid json");
        }
        catch (IOException ex)
        {
            return AdminJsonError(500, "failed to save setting", ex.Message);
        }
    }

    [HttpGet]
    [Route("/adminpanel/api/sync-backup/download")]
    public ActionResult SyncBackupDownload(string name)
    {
        // имя проверено регуляркой, выйти из каталога нельзя
        if (name == null || !SyncBackup.NameRx.IsMatch(name))
            return AdminJsonError(400, "invalid backup name");

        var path = Path.GetFullPath(Path.Combine(SyncBackup.Dir, name));
        if (!System.IO.File.Exists(path))
            return AdminJsonError(404, "backup not found");

        return PhysicalFile(path, "application/octet-stream", name);
    }

    [HttpPost]
    [Route("/adminpanel/api/sync-backup/delete")]
    public async Task<IActionResult> SyncBackupDelete()
    {
        string name;
        try
        {
            name = (string)JObject.Parse(await ReadBodyAsync().ConfigureAwait(false))["name"];
        }
        catch (JsonException)
        {
            return AdminJsonError(400, "invalid json");
        }

        if (name == null || !SyncBackup.NameRx.IsMatch(name))
            return AdminJsonError(400, "invalid backup name");

        var path = Path.Combine(SyncBackup.Dir, name);
        if (!System.IO.File.Exists(path))
            return AdminJsonError(404, "backup not found");

        try
        {
            System.IO.File.Delete(path);
            return AdminJsonOk();
        }
        catch (IOException ex)
        {
            return AdminJsonError(500, "failed to delete", ex.Message);
        }
    }
    #endregion

    #region repository.yaml
    const string SecretMask = "***KEEP***";
    static readonly Regex SecretLine = new(
        @"^(?<head>\s*(?:-\s+)?(?:token|pat|personal_access_token|auth_header|authorization|authorization_header)\s*:\s*)(?<val>\S.*?)(?<tail>\s*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // как в ModuleRepository: mods/repository.yaml, если он есть, иначе module/repository.yaml
    static string RepoFile => System.IO.File.Exists(Path.Combine("mods", "repository.yaml"))
        ? Path.Combine("mods", "repository.yaml")
        : Path.Combine("module", "repository.yaml");

    [HttpGet]
    [Route("/adminpanel/api/repository")]
    public ActionResult RepositoryGet()
    {
        var file = RepoFile;
        var exists = System.IO.File.Exists(file);
        var text = exists ? System.IO.File.ReadAllText(file, Encoding.UTF8) : "";
        var secrets = 0;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = SecretLine.Match(lines[i]);
            if (m.Success)
            {
                secrets++;
                lines[i] = m.Groups["head"].Value + SecretMask + m.Groups["tail"].Value;
            }
        }

        var o = new JObject
        {
            ["file"] = file.Replace('\\', '/'),
            ["exists"] = exists,
            ["text"] = string.Join("\n", lines),
            ["secrets"] = secrets,
            ["mask"] = SecretMask
        };
        return Content(o.ToString(Formatting.None), "application/json; charset=utf-8");
    }

    // Тело: {"text":"..."}. Значения токенов ***KEEP*** подставляются из текущего файла по порядку.
    [HttpPost]
    [Route("/adminpanel/api/repository")]
    public async Task<IActionResult> RepositorySave()
    {
        string text;
        try
        {
            text = (string)JObject.Parse(await ReadBodyAsync().ConfigureAwait(false))["text"];
        }
        catch (JsonException)
        {
            return AdminJsonError(400, "invalid json");
        }

        if (text == null)
            return AdminJsonError(400, "text is required");

        var file = RepoFile;
        var original = System.IO.File.Exists(file)
            ? System.IO.File.ReadAllText(file, Encoding.UTF8).Replace("\r\n", "\n").Split('\n')
            : Array.Empty<string>();
        var secrets = original.Select(l => SecretLine.Match(l)).Where(m => m.Success).Select(m => m.Groups["val"].Value).ToList();

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var idx = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var m = SecretLine.Match(lines[i]);
            if (!m.Success)
                continue;

            if (m.Groups["val"].Value == SecretMask)
            {
                if (idx >= secrets.Count)
                    return AdminJsonError(400, "token placeholder has no original value", "line " + (i + 1));

                lines[i] = m.Groups["head"].Value + secrets[idx] + m.Groups["tail"].Value;
            }

            idx++;
        }

        var final = string.Join("\n", lines);

        try
        {
            // только проверка синтаксиса; смысл полей проверяет ModuleRepository при старте
            new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<object>(new StringReader(final));
        }
        catch (Exception ex)
        {
            return AdminJsonError(400, "invalid yaml", ex.Message);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file) ?? "module");
            var backup = await WriteAtomicAsync(file, final).ConfigureAwait(false);
            return AdminJsonOk(backup);
        }
        catch (IOException ex)
        {
            return AdminJsonError(500, "failed to write repository.yaml", ex.Message);
        }
    }
    #endregion
}
