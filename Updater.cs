using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace StutterFix
{
    // 새 버전 알림과 업데이트 받기.
    // 게임을 켜고 조금 뒤 GitHub 의 최신 릴리스(api.github.com/repos/pding4569/StutterFix/releases/latest)를 한 번 확인한다.
    // 더 새 버전이 있으면 설정 창 홈 / 정보 페이지와 첫 화면 안내에 띄우고, 버튼을 누르면 그 릴리스의 zip(플레이어용 또는 개발자용)을 받아
    // 모드 폴더의 StutterFix.dll 과 Info.json 을 바꾼다. 게임이 쓰는 DLL 은 UMM 이 복사본을 올려 두므로 바로 덮어쓸 수 있고,
    // 새 버전은 게임을 다시 켜면 올라온다(재시작 안내가 자동으로 뜬다). 원래 DLL 은 StutterFix.dll.bak 으로 남긴다.
    // UMM 도 Info.json 의 Repository(repository.json)로 모드 목록에 새 버전을 표시한다. 릴리스할 때 repository.json 버전을 같이 올린다.
    internal static class Updater
    {
        private const string Api = "https://api.github.com/repos/pding4569/StutterFix/releases/latest";
        private const string DownloadPrefix = "https://github.com/pding4569/StutterFix/releases/download/";

        internal static string Latest = "";        // 최신 릴리스 버전 (예: 2.1.1)
        internal static string NotesUrl = "";      // 릴리스 페이지
        internal static string Status = "";        // 화면에 보일 상태
        internal static bool Available;             // 지금 버전보다 새 버전이 있음
        internal static bool Installed;             // 받아서 바꿔 둠 (다시 켜면 적용)
        internal static bool Busy;
        private static string zipUrl = "";
        private static UnityWebRequest req;
        private static bool downloading, checkedOnce;
        private static float startAt = -1f;

        private static string Current { get { try { return Main.Entry.Info.Version; } catch { return "0"; } } }

        // OnUpdate 에서 부른다
        internal static void Tick()
        {
            if (req != null) { if (req.isDone) Finish(); return; }
            if (checkedOnce || Main.Config == null || !Main.Config.CheckUpdates) return;
            if (startAt < 0f) startAt = Time.realtimeSinceStartup;
            if (Hitch.Playing || Time.realtimeSinceStartup - startAt < 15f) return;   // 켜진 직후와 플레이 중에는 하지 않는다
            Check();
        }

        internal static void Check()
        {
            if (req != null) return;
            checkedOnce = true;
            try
            {
                req = UnityWebRequest.Get(Api);
                req.SetRequestHeader("User-Agent", "StutterFix/" + Current);
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.timeout = 20;
                req.SendWebRequest();
                downloading = false; Busy = true;
                Status = SettingsWindow.T("새 버전 확인 중…", "Checking for updates…");
            }
            catch (Exception ex) { req = null; Busy = false; Status = SettingsWindow.T("확인 실패: ", "Check failed: ") + ex.Message; }
        }

        internal static void Download()
        {
            if (req != null || !Available || zipUrl.Length == 0) return;
            try
            {
                req = UnityWebRequest.Get(zipUrl);
                req.SetRequestHeader("User-Agent", "StutterFix/" + Current);
                req.timeout = 120;
                req.SendWebRequest();
                downloading = true; Busy = true;
                Status = SettingsWindow.T("받는 중…", "Downloading…");
            }
            catch (Exception ex) { req = null; Busy = false; Status = SettingsWindow.T("받기 실패: ", "Download failed: ") + ex.Message; }
        }

        private static void Finish()
        {
            var r = req; req = null; Busy = false;
            try
            {
                if (r.result != UnityWebRequest.Result.Success)
                {
                    Status = (downloading ? SettingsWindow.T("받기 실패: ", "Download failed: ") : SettingsWindow.T("확인 실패: ", "Check failed: ")) + r.error;
                    Main.Entry.Logger.Log("[업데이트] " + Status);
                    return;
                }
                if (downloading) Install(r.downloadHandler.data);
                else Parse(r.downloadHandler.text);
            }
            catch (Exception ex) { Status = SettingsWindow.T("실패: ", "Failed: ") + ex.Message; Main.Entry.Logger.Log("[업데이트] 실패: " + ex); }
            finally { r.Dispose(); }
        }

        private static void Parse(string json)
        {
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9][0-9.]*)\"");
            if (!tag.Success) { Status = SettingsWindow.T("릴리스 정보를 읽지 못했습니다", "Could not read release info"); return; }
            Latest = tag.Groups[1].Value;
            var page = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github.com/pding4569/StutterFix/releases/[^\"]+)\"");
            NotesUrl = page.Success ? page.Groups[1].Value : "https://github.com/pding4569/StutterFix/releases";
            string want = "StutterFix-" + Latest + "-" + (Edition.Dev ? "developer" : "player") + ".zip";
            zipUrl = "";
            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                string u = m.Groups[1].Value;
                if (u.StartsWith(DownloadPrefix, StringComparison.Ordinal) && u.EndsWith("/" + want, StringComparison.Ordinal)) { zipUrl = u; break; }
            }
            Notes = CleanNotes(JsonString(json, "body"));
            Available = Newer(Latest, Current);
            Status = Available
                ? string.Format(SettingsWindow.T("새 버전 v{0} 이 있습니다 (지금 v{1})", "New version v{0} available (you have v{1})"), Latest, Current)
                : string.Format(SettingsWindow.T("최신 버전입니다 (v{0})", "Up to date (v{0})"), Current);
            if (Available && zipUrl.Length == 0) Status += SettingsWindow.T(" - 받을 파일이 없어 릴리스 페이지에서 직접 받아야 합니다", " - no download file; get it from the release page");
            Main.Entry.Logger.Log("[업데이트] 최신 v" + Latest + ", 지금 v" + Current + (Available ? " -> 새 버전 있음" : ""));
        }

        internal static string Notes = "";   // 패치노트 (릴리스 본문, 마크다운 기호를 걷어낸 것)

        // JSON 안의 문자열 값 하나 (이스케이프 풀기)
        private static string JsonString(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"");
            if (!m.Success) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = m.Index + m.Length; i < json.Length; i++)
            {
                char ch = json[i];
                if (ch == '"') break;
                if (ch != '\\' || i + 1 >= json.Length) { sb.Append(ch); continue; }
                char e = json[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': break;
                    case 't': sb.Append("  "); break;
                    case 'u':
                        if (i + 4 < json.Length) { try { sb.Append((char)Convert.ToInt32(json.Substring(i + 1, 4), 16)); } catch { } i += 4; }
                        break;
                    default: sb.Append(e); break;   // \" \\ \/
                }
            }
            return sb.ToString();
        }

        // 마크다운을 창에 보이기 좋게: 제목 #, 굵게 **, 코드 `, 링크 [글](주소) -> 글, 목록 - -> ·
        private static string CleanNotes(string md)
        {
            if (string.IsNullOrEmpty(md)) return "";
            var lines = md.Replace("\r", "").Split('\n');
            var sb = new System.Text.StringBuilder();
            foreach (var raw in lines)
            {
                string l = raw.TrimEnd();
                l = Regex.Replace(l, "^#+\\s*", "");
                l = Regex.Replace(l, "^(\\s*)[-*]\\s+", "$1· ");
                l = Regex.Replace(l, "\\[([^\\]]+)\\]\\([^)]+\\)", "$1");
                l = l.Replace("**", "").Replace("`", "");
                if (l.Length == 0 && (sb.Length == 0 || sb.ToString().EndsWith("\n\n"))) continue;   // 빈 줄은 하나만
                sb.Append(l).Append('\n');
            }
            string s = sb.ToString().Trim();
            if (s.Length > 2500) s = s.Substring(0, 2500) + "\n…";
            return s;
        }

        private static bool Newer(string a, string b)
        {
            try { return new Version(Pad(a)) > new Version(Pad(b)); } catch { return false; }
        }
        private static string Pad(string v) { var p = v.Split('.'); return p.Length >= 2 ? v : v + ".0"; }

        private static void Install(byte[] zip)
        {
            byte[] dll = null; string info = null;
            using (var ms = new MemoryStream(zip))
            using (var za = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var e in za.Entries)
                {
                    string n = e.FullName.Replace('\\', '/');
                    if (n == "StutterFix/StutterFix.dll") dll = ReadAll(e);
                    else if (n == "StutterFix/Info.json") info = System.Text.Encoding.UTF8.GetString(ReadAll(e));
                }
            }
            // 받은 것이 맞는지: DLL 모양(MZ), Info.json 의 Id 와 버전
            if (dll == null || dll.Length < 1024 || dll[0] != (byte)'M' || dll[1] != (byte)'Z') throw new Exception("zip 안에 올바른 StutterFix.dll 이 없음");
            if (info == null || !Regex.IsMatch(info, "\"Id\"\\s*:\\s*\"StutterFix\"") || !Regex.IsMatch(info, "\"Version\"\\s*:\\s*\"" + Regex.Escape(Latest) + "\""))
                throw new Exception("zip 안의 Info.json 이 맞지 않음");
            string dir = Main.Entry.Path;
            string dllPath = Path.Combine(dir, "StutterFix.dll"), infoPath = Path.Combine(dir, "Info.json");
            try { if (File.Exists(dllPath)) File.Copy(dllPath, dllPath + ".bak", true); } catch { }
            File.WriteAllBytes(dllPath + ".new", dll);
            File.Copy(dllPath + ".new", dllPath, true);
            File.Delete(dllPath + ".new");
            File.WriteAllText(infoPath, info);
            Installed = true; Available = false;
            Status = string.Format(SettingsWindow.T("v{0} 을 받았습니다. 게임을 다시 켜면 적용됩니다", "Downloaded v{0}. Restart the game to apply"), Latest);
            Main.Entry.Logger.Log("[업데이트] v" + Latest + " 설치 (다시 켜면 적용)");
        }

        private static byte[] ReadAll(ZipArchiveEntry e)
        {
            using (var s = e.Open())
            using (var o = new MemoryStream())
            {
                s.CopyTo(o);
                return o.ToArray();
            }
        }

        // 첫 화면 안내: 새 버전이 있으면 곡 밖에서 20초 동안 오른쪽 위에 작게 (설정 창이 닫혀 있을 때)
        private static float noticeUntil = -1f;
        private static GUIStyle noticeStyle;
        internal static void DrawNotice(bool windowOpen)
        {
            if (!Available || windowOpen || Hitch.Playing || Event.current.type != EventType.Repaint) return;
            if (noticeUntil < 0f) noticeUntil = Time.realtimeSinceStartup + 20f;
            if (Time.realtimeSinceStartup > noticeUntil) return;
            if (noticeStyle == null)
            {
                noticeStyle = new GUIStyle(GUI.skin.label) { font = SettingsWindow.UiFont(), fontSize = 15, alignment = TextAnchor.MiddleCenter, wordWrap = false };
                noticeStyle.normal.textColor = Color.white;
            }
            string key = Hotkey.Name(Main.Config.WindowKey, Main.Config.WindowMods);
            var text = new GUIContent(string.Format(SettingsWindow.T("Stutter Fix 새 버전 v{0} · {1} 키 → 홈에서 업데이트", "Stutter Fix v{0} available · press {1} → Home to update"), Latest, key));
            var size = noticeStyle.CalcSize(text);
            var r = new Rect(Screen.width - size.x - 44, 20, size.x + 28, 40);
            var old = GUI.color;
            GUI.color = new Color(0.06f, 0.065f, 0.08f, 0.88f);
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, GUI.color, 0, 10);
            GUI.color = old;
            GUI.Label(r, text, noticeStyle);
        }
    }
}
