// Copyright (c) 2026 thezencirs (https://github.com/thezencirs)
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CircleLine
{
    static class Updater
    {
        public static string Repo = "thezencirs/CircleLine";
        public static string ApiBase = "https://api.github.com";
        public const string ExeName = "CircleLine.exe";

        public class Release
        {
            public Version Version;
            public string Tag, Notes, ZipUrl, SumsUrl;
        }

        public static Version Current
        {
            get { Version v = Assembly.GetExecutingAssembly().GetName().Version; return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }
        }

        public static string CurrentText { get { return Current.ToString(); } }

        public static bool Configured { get { return !Repo.StartsWith("OWNER/", StringComparison.Ordinal); } }

        public static void CleanupOld()
        {
            try { string old = Application.ExecutablePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
        }

        static HttpWebRequest NewRequest(string url)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls;
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = "CircleLine-Updater/" + CurrentText;
            rq.Accept = "application/vnd.github+json";
            rq.Timeout = 15000;
            rq.ReadWriteTimeout = 30000;
            return rq;
        }

        static string Unescape(string s)
        {
            return Regex.Replace(s, @"\\(u[0-9a-fA-F]{4}|.)", delegate (Match m)
            {
                string c = m.Groups[1].Value;
                if (c[0] == 'u') return ((char)Convert.ToInt32(c.Substring(1), 16)).ToString();
                switch (c[0]) { case 'n': return "\n"; case 'r': return ""; case 't': return "\t"; default: return c; }
            });
        }

        public static Release Fetch(out string error)
        {
            error = null;
            try
            {
                string json;
                using (WebResponse resp = NewRequest(ApiBase + "/repos/" + Repo + "/releases/latest").GetResponse())
                using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) json = r.ReadToEnd();

                Match tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                if (!tag.Success) { error = "Sürüm bilgisi okunamadı."; return null; }
                Release rel = new Release();
                rel.Tag = tag.Groups[1].Value;
                try { rel.Version = new Version(rel.Tag.TrimStart('v', 'V')); }
                catch { error = "Sürüm etiketi anlaşılamadı: " + rel.Tag; return null; }
                Match body = Regex.Match(json, "\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                rel.Notes = body.Success ? Unescape(body.Groups[1].Value).Trim() : "";
                foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
                {
                    string u = m.Groups[1].Value;
                    if (Regex.IsMatch(u, @"/CircleLine-v[^/]*-win\.zip$", RegexOptions.IgnoreCase)) rel.ZipUrl = u;
                    else if (u.EndsWith("/SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) rel.SumsUrl = u;
                }
                return rel;
            }
            catch (WebException ex)
            {
                HttpWebResponse hr = ex.Response as HttpWebResponse;
                error = hr != null && hr.StatusCode == HttpStatusCode.NotFound ? "Henüz yayınlanmış bir sürüm bulunamadı." : "Sunucuya ulaşılamadı: " + ex.Message;
                return null;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        public static bool IsNewer(Release r) { return r != null && r.Version > Current; }

        static void Download(string url, string path)
        {
            using (WebResponse resp = NewRequest(url).GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (FileStream f = File.Create(path)) s.CopyTo(f);
        }

        public static bool Apply(Release rel, out string error)
        {
            error = null;
            string dir = Path.Combine(Path.GetTempPath(), "CircleLine_update_" + Guid.NewGuid().ToString("N"));
            try
            {
                if (rel.ZipUrl == null || rel.SumsUrl == null) { error = "Sürümde zip veya SHA256SUMS.txt eksik; güvenlik için güncelleme yapılmadı."; return false; }
                Directory.CreateDirectory(dir);
                string zip = Path.Combine(dir, "update.zip"), sums = Path.Combine(dir, "SHA256SUMS.txt");
                Download(rel.ZipUrl, zip);
                Download(rel.SumsUrl, sums);

                string zipName = Uri.UnescapeDataString(rel.ZipUrl.Substring(rel.ZipUrl.LastIndexOf('/') + 1));
                string expected = null;
                foreach (string line in File.ReadAllLines(sums))
                {
                    Match m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
                    if (m.Success && string.Equals(m.Groups[2].Value.Trim(), zipName, StringComparison.OrdinalIgnoreCase)) expected = m.Groups[1].Value.ToLowerInvariant();
                }
                if (expected == null) { error = "SHA256SUMS.txt içinde " + zipName + " bulunamadı; güncelleme yapılmadı."; return false; }
                string actual;
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = File.OpenRead(zip))
                    actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                if (actual != expected) { error = "İndirilen dosyanın SHA256 özeti uyuşmuyor; güncelleme iptal edildi."; return false; }

                string newExe = Path.Combine(dir, ExeName);
                using (ZipArchive za = ZipFile.OpenRead(zip))
                {
                    ZipArchiveEntry e = null;
                    foreach (ZipArchiveEntry x in za.Entries) if (string.Equals(x.Name, ExeName, StringComparison.OrdinalIgnoreCase)) e = x;
                    if (e == null) { error = "Zip içinde " + ExeName + " yok."; return false; }
                    e.ExtractToFile(newExe, true);
                }
                if (new FileInfo(newExe).Length < 8 * 1024) { error = "İndirilen exe geçersiz görünüyor."; return false; }

                string exe = Application.ExecutablePath, old = exe + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
                try { File.Copy(newExe, exe); }
                catch { File.Move(old, exe); throw; }
                Process.Start(new ProcessStartInfo(exe, "--after-update") { UseShellExecute = false });
                return true;
            }
            catch (UnauthorizedAccessException) { error = "Bu klasöre yazma izni yok. Yeni sürümü GitHub'dan elle indirin."; return false; }
            catch (Exception ex) { error = ex.Message; return false; }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
