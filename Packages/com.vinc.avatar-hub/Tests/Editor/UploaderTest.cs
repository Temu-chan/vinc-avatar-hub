using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace VInc.AvatarHub.Tests
{
    /// <summary>
    /// 開発用：⑦ アバターアップローダーを確かめる。実際には VRChat へアップロードしない（SDK の部分は偽物に差し替える）。
    /// 本物の SDK 部分は、登録・アバターの判定・Blueprint ID の読み取り・キューへの追加だけ確かめる（通信しない）。
    /// Temp/viah_upload.request を置いてスクリプトを再読み込みすると実行し、Temp/viah_upload.log に結果を書く。
    /// </summary>
    [InitializeOnLoad]
    static class UploaderTest
    {
        const string Request = "Temp/viah_upload.request";
        const string LogPath = "Temp/viah_upload.log";
        const string SceneDir = "Assets/VIAH_UploadTest";
        static readonly List<string> log = new List<string>();
        static int pass, fail;

        static UploaderTest()
        {
            if (!File.Exists(Request)) return;
            File.Delete(Request);
            EditorApplication.delayCall += Run;
        }

        static void Check(bool ok, string what)
        {
            if (ok) pass++;
            else fail++;
            log.Add((ok ? "OK   " : "NG   ") + what);
        }

        // ───────────── 偽物の SDK ─────────────

        class Fake : IAvatarUploadBackend
        {
            public readonly Dictionary<GameObject, string> ids = new Dictionary<GameObject, string>();
            /// <summary>シーンを開き直すと別の GameObject になるので、名前でも引けるようにする。</summary>
            public readonly Dictionary<string, string> idsByName = new Dictionary<string, string>();
            public readonly List<string> events = new List<string>();
            public readonly HashSet<string> failPrepare = new HashSet<string>();
            public readonly HashSet<string> failUpload = new HashSet<string>();
            public readonly HashSet<string> decline = new HashSet<string>();
            public readonly HashSet<string> unchanged = new HashSet<string>();
            public readonly HashSet<string> slow = new HashSet<string>();
            public readonly Dictionary<string, bool> activeAtUpload = new Dictionary<string, bool>();
            /// <summary>アップロードしたときにアバターがあったシーン。</summary>
            public readonly Dictionary<string, string> sceneAtUpload = new Dictionary<string, string>();
            public bool panelOpen = true, loggedIn = true, openWorks = true, cancelled, saveSceneDuringUpload, cancelAsError;

            public string SdkVersion => "fake";
            public List<GameObject> FindAvatars(GameObject root) =>
                root == null ? new List<GameObject>() : root.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject).Where(g => g.name.StartsWith("Avatar")).ToList();
            public string BlueprintId(GameObject avatar) =>
                avatar == null ? "" : ids.TryGetValue(avatar, out var id) ? id : idsByName.TryGetValue(avatar.name, out var byName) ? byName : "";
            public bool PanelOpen => panelOpen;
            public void OpenPanel()
            {
                if (openWorks) panelOpen = true;
                events.Add("open");
            }
            public bool LoggedIn => loggedIn;
            public string UserName => "tester";
            public string Platform => "Windows（PC）";

            public async Task<string> Prepare(string id, CancellationToken ct)
            {
                await Task.Delay(5);
                events.Add("prepare:" + id);
                if (failPrepare.Contains(id)) throw new AvatarUploadException("VRChat にこのアバターが見つかりません");
                return "Remote " + id;
            }

            public async Task Agree(string id, CancellationToken ct)
            {
                await Task.Delay(5);
                events.Add("agree:" + id);
                if (decline.Contains(id)) throw new AvatarUploadException("著作権の同意をしなかったので飛ばしました", "declined");
            }

            public async Task Upload(GameObject avatar, Action<string, float, bool> report, CancellationToken ct)
            {
                var id = BlueprintId(avatar);
                events.Add("upload:" + id);
                activeAtUpload[id] = avatar.activeInHierarchy;
                sceneAtUpload[id] = avatar.scene.path;
                if (saveSceneDuringUpload) EditorSceneManager.SaveScene(avatar.scene);
                report("ビルド中", -1, false);
                await Task.Delay(10);
                if (slow.Contains(id))
                {
                    for (int i = 0; i < 200 && slow.Contains(id) && !ct.IsCancellationRequested && !cancelled; i++) await Task.Delay(10);
                }
                if (ct.IsCancellationRequested || cancelled)
                {
                    if (cancelAsError) throw new AvatarUploadException("アップロードに失敗しました：Request Cancelled");
                    throw new OperationCanceledException();
                }
                report("アップロード中", 0.5f, true);
                await Task.Delay(10);
                if (failUpload.Contains(id)) throw new AvatarUploadException("SDK のチェックでエラーがあります：テスト");
                if (unchanged.Contains(id)) throw new AvatarUploadException("前回と中身が同じなので、アップロードは省かれました", "unchanged");
                report("アップロード中", 1, true);
                events.Add("uploaded:" + id);
            }

            public void CancelUpload() => cancelled = true;
        }

        // ───────────── 手順 ─────────────

        /// <summary>手順は「終わったら true」を返す。Task の続きはエディターの更新で進むので、毎フレーム見る。</summary>
        static readonly Queue<(string name, Func<bool> step)> steps = new Queue<(string, Func<bool>)>();
        static double stepStarted;
        static string work;
        static IAvatarUploadBackend real;
        static Fake fake;
        static Scene testScene;
        static readonly List<bool> sounds = new List<bool>();
        static readonly List<string> seenStatus = new List<string>();
        static string savedDir;

        static void Run()
        {
            log.Clear();
            pass = fail = 0;
            steps.Clear();
            work = Path.GetFullPath("Temp/viah_upload");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            ViahStorage.DataDirOverride = Path.Combine(work, "data");
            ViahDatabase.Reload();
            var st = ViahDatabase.Settings;
            st.soundOn = true;
            st.soundSkipShort = false;
            st.soundVolume = 0;
            st.soundMuted.Clear();
            savedDir = AvatarUploader.DirOverride;
            AvatarUploader.DirOverride = Path.Combine(work, "lib");
            AvatarUploader.ResetCache();
            real = AvatarUploader.Backend;
            // バッチモードでは名前の無いシーンで始まるので、保存しておく（シーンを追加で作れるように）
            if (Application.isBatchMode && SceneManager.GetActiveScene().path == "")
            {
                if (!AssetDatabase.IsValidFolder(SceneDir)) AssetDatabase.CreateFolder("Assets", "VIAH_UploadTest");
                EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), SceneDir + "/Base.unity");
            }
            ViahSound.Played -= OnPlayed;
            ViahSound.Played += OnPlayed;
            AvatarUploader.Changed -= OnChanged;
            AvatarUploader.Changed += OnChanged;

            Do("本物の SDK 部分", RealBackend);
            Do("偽物に差し替え", UseFake);
            Scenario("全部成功（同意を先に全部）", () => { }, s => { }, AllSucceed);
            Scenario("失敗は飛ばして次へ", () => fake.failUpload.Add("avtr_2"), s => { }, SkipFailure);
            Scenario("失敗したら止める", () => fake.failUpload.Add("avtr_2"), s => s.stopOnError = true, StopOnError);
            Scenario("同意しなかったものは飛ばす", () => { fake.decline.Add("avtr_3"); fake.failPrepare.Add("avtr_4"); }, s => { }, Declined);
            Scenario("前回と同じ中身", () => fake.unchanged.Add("avtr_1"), s => { }, Unchanged);
            Scenario("ログインしていない", () => { fake.loggedIn = false; AvatarUploader.WaitSeconds = 0.5; }, s => { }, NotLoggedIn);
            SlowScenario("今すぐ中止 → 続きを開始", true);
            SlowScenario("いまのが終わったら止める", false);
            HiddenSteps();
            Do("再読み込みで中断", Recover);
            Do("保存と読み直し", Persist);
            Do("SDK が無いとき", NoSdk);
            SceneSwitchSteps();
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void OnPlayed(bool attention) => sounds.Add(attention);

        static void OnChanged()
        {
            foreach (var j in AvatarUploader.Session.jobs)
                if (seenStatus.Count == 0 || seenStatus[seenStatus.Count - 1] != j.id + ":" + j.status) seenStatus.Add(j.id + ":" + j.status);
        }

        static void Do(string name, Action a) => steps.Enqueue((name, () =>
        {
            a();
            return true;
        }));

        static void Wait(string name, Func<bool> until) => steps.Enqueue((name, until));

        static void Tick()
        {
            if (steps.Count == 0)
            {
                Finish();
                return;
            }
            var (name, step) = steps.Peek();
            if (stepStarted == 0) stepStarted = EditorApplication.timeSinceStartup;
            bool done;
            try
            {
                done = step();
            }
            catch (Exception e)
            {
                Check(false, $"{name}：例外 {e.GetType().Name} {e.Message}\n{e.StackTrace}");
                done = true;
            }
            if (!done && EditorApplication.timeSinceStartup - stepStarted > 30)
            {
                Check(false, name + "：時間切れ");
                done = true;
            }
            if (!done) return;
            steps.Dequeue();
            stepStarted = 0;
        }

        static void Finish()
        {
            EditorApplication.update -= Tick;
            ViahSound.Played -= OnPlayed;
            AvatarUploader.Changed -= OnChanged;
            try
            {
                if (testScene.IsValid() && testScene.isLoaded) EditorSceneManager.CloseScene(testScene, true);
            }
            catch (Exception e)
            {
                log.Add("シーンを閉じられませんでした: " + e.Message);
            }
            AvatarUploader.Backend = real;
            AvatarUploader.DirOverride = savedDir;
            AvatarUploader.ResetCache();
            AvatarUploader.WaitSeconds = 20;
            ViahStorage.DataDirOverride = null;
            ViahDatabase.Reload();
            if (AssetDatabase.IsValidFolder(SceneDir)) AssetDatabase.DeleteAsset(SceneDir);
            log.Insert(0, $"pass={pass} fail={fail}");
            File.WriteAllLines(LogPath, log);
            Debug.Log($"[Avatar Hub] アップローダーのテスト：pass={pass} fail={fail}");
        }

        // ───────────── テスト用のシーン ─────────────

        static GameObject root;
        static readonly List<GameObject> avatars = new List<GameObject>();

        static void MakeScene()
        {
            if (testScene.IsValid() && testScene.isLoaded) EditorSceneManager.CloseScene(testScene, true);
            try
            {
                testScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            }
            catch (Exception e)
            {
                log.Add("新しいシーンを作れないので、今のシーンに置きます: " + e.Message);
                testScene = SceneManager.GetActiveScene();
            }
            root = new GameObject("VIAH_UploadTest");
            SceneManager.MoveGameObjectToScene(root, testScene);
            avatars.Clear();
            for (int i = 1; i <= 5; i++)
            {
                var a = new GameObject("Avatar" + i);
                a.transform.SetParent(root.transform);
                avatars.Add(a);
            }
        }

        static Type FindType(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName)).FirstOrDefault(t => t != null);

        static void RealBackend()
        {
            Check(real != null && real.GetType().FullName == "VInc.AvatarHub.VRChat.VrcAvatarUploadBackend", "VRChat SDK があると、SDK 部分が登録される：" + real?.GetType().FullName);
            if (real == null) return;
            Check(!string.IsNullOrEmpty(real.SdkVersion), "SDK のバージョン：" + real.SdkVersion);
            Check(!string.IsNullOrEmpty(real.Platform), "アップロード先：" + real.Platform);
            var desc = FindType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            var pmType = FindType("VRC.Core.PipelineManager");
            Check(desc != null && pmType != null, "VRC Avatar Descriptor と Pipeline Manager の型");
            if (desc == null || pmType == null) return;
            MakeScene();
            var parent = new GameObject("Group");
            SceneManager.MoveGameObjectToScene(parent, testScene);
            var withId = new GameObject("RealAvatarA");
            withId.transform.SetParent(parent.transform);
            withId.AddComponent(desc);
            var pm = withId.GetComponent(pmType) ?? withId.AddComponent(pmType);
            pmType.GetField("blueprintId").SetValue(pm, "avtr_viah_test_0001");
            var noId = new GameObject("RealAvatarB");
            noId.transform.SetParent(parent.transform);
            noId.AddComponent(desc);
            var plain = new GameObject("NotAvatar");
            SceneManager.MoveGameObjectToScene(plain, testScene);

            var found = real.FindAvatars(parent);
            Check(found.Count == 2 && found.Contains(withId) && found.Contains(noId), $"親から中のアバターを探す（{found.Count} 体）");
            Check(real.FindAvatars(plain).Count == 0, "アバターでないものは見つからない");
            Check(real.BlueprintId(withId) == "avtr_viah_test_0001", "Blueprint ID を読む：" + real.BlueprintId(withId));
            Check(real.BlueprintId(noId) == "", "Blueprint ID が無いときは空");

            var problems = new List<string>();
            int added = AvatarUploader.Add(new[] { parent, plain }, problems);
            Check(added == 1 && AvatarUploader.Session.jobs.Count == 1, $"アップロードしたことのあるものだけ追加（{added} 体）");
            Check(problems.Any(p => p.Contains("RealAvatarB") && p.Contains("一度も")), "未アップロードは理由つきで入れない");
            Check(problems.Any(p => p.Contains("NotAvatar") && p.Contains("アバターではありません")), "アバターでないものは理由つきで入れない");
            var j = AvatarUploader.Session.jobs.FirstOrDefault();
            Check(j != null && j.objectPath == "Group/RealAvatarA" && j.blueprintId == "avtr_viah_test_0001", "場所と ID を覚える：" + j?.objectPath);
            problems.Clear();
            Check(AvatarUploader.Add(new[] { withId }, problems) == 0 && problems.Any(p => p.Contains("すでに")), "同じアバターは重ねて入れない");
            Check(AvatarUploader.Find(j) == withId, "キューの行からシーンのアバターを見つける");
            var prefabProblems = new List<string>();
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.FindAssets("t:Prefab").Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault() ?? "");
            if (asset != null)
            {
                AvatarUploader.Add(new[] { asset }, prefabProblems);
                Check(prefabProblems.Any(p => p.Contains("プレハブ")), "プレハブ（ファイル）は入れない");
            }
            AvatarUploader.Session.jobs.Clear();
            AvatarUploader.Save();
        }

        static void UseFake()
        {
            fake = new Fake();
            AvatarUploader.Backend = fake;
            MakeScene();
        }

        /// <summary>偽物と 5 体のキューを用意して始め、終わるのを待って確かめる。</summary>
        static void Scenario(string name, Action arrange, Action<UploadSession> options, Action<UploadSession> verify)
        {
            Do(name + "：準備", () =>
            {
                fake = new Fake();
                AvatarUploader.Backend = fake;
                AvatarUploader.WaitSeconds = 20;
                MakeScene();
                for (int i = 0; i < avatars.Count; i++) fake.ids[avatars[i]] = "avtr_" + (i + 1);
                var s = AvatarUploader.Session;
                s.jobs.Clear();
                s.stopOnError = false;
                s.notice = "";
                var problems = new List<string>();
                AvatarUploader.Add(new[] { root }, problems);
                arrange();
                options(s);
                sounds.Clear();
                seenStatus.Clear();
                Check(AvatarUploader.CannotStart() == null, name + "：始められる " + AvatarUploader.CannotStart());
                AvatarUploader.Start();
                Check(AvatarUploader.Session.running, name + "：実行中になる");
            });
            Wait(name + "：終わるのを待つ", () => AvatarUploader.Current.IsCompleted && !AvatarUploader.Working);
            Do(name + "：確認", () => verify(AvatarUploader.Session));
        }

        static int Count(UploadSession s, string status) => s.jobs.Count(j => j.status == status);

        static void AllSucceed(UploadSession s)
        {
            Check(Count(s, "done") == 5, "5 体とも完了");
            var ev = fake.events;
            int lastAgree = ev.FindLastIndex(e => e.StartsWith("agree:"));
            int firstUpload = ev.FindIndex(e => e.StartsWith("upload:"));
            Check(lastAgree >= 0 && firstUpload > lastAgree, "全員分の同意が終わってからアップロードを始める");
            var order = ev.Where(e => e.StartsWith("upload:")).Select(e => e.Substring(7)).ToList();
            Check(order.SequenceEqual(new[] { "avtr_1", "avtr_2", "avtr_3", "avtr_4", "avtr_5" }), "キューの順番にアップロード：" + string.Join(",", order));
            Check(ev.Count > 0 && ev[0] == "open", "SDK の画面がすでに開いていても、始めるときに前に出す：" + ev.FirstOrDefault());
            Check(seenStatus.Any(x => x.EndsWith(":agreeing")) && seenStatus.Any(x => x.EndsWith(":building")) && seenStatus.Any(x => x.EndsWith(":uploading")), "同意待ち → ビルド中 → アップロード中 を通る");
            Check(s.jobs.All(j => j.remoteName == "Remote " + j.blueprintId), "VRChat 上の名前を覚える");
            Check(s.jobs.All(j => !string.IsNullOrEmpty(j.finishedAt)), "終わった時刻を覚える");
            Check(!s.running, "終わると実行中でなくなる");
            Check(sounds.SequenceEqual(new[] { false }), "全部終わったら「終わった」の音：" + string.Join(",", sounds));
            Check(s.Complete, "キューが全部終わった扱い");
        }

        static void SkipFailure(UploadSession s)
        {
            var failed = s.jobs.Find(j => j.blueprintId == "avtr_2");
            Check(failed.status == "failed" && failed.message.Contains("SDK のチェック"), "失敗した行に理由を出す：" + failed.message);
            Check(Count(s, "done") == 4, "ほかの 4 体は続けてアップロード");
            Check(sounds.SequenceEqual(new[] { true }), "失敗があると「確認して」の音：" + string.Join(",", sounds));
        }

        static void StopOnError(UploadSession s)
        {
            Check(s.jobs.Find(j => j.blueprintId == "avtr_1").status == "done", "1 体目は完了");
            Check(s.jobs.Find(j => j.blueprintId == "avtr_2").status == "failed", "2 体目で失敗");
            Check(s.jobs.Skip(2).All(j => j.status == "waiting"), "3 体目からは待機中に戻る：" + string.Join(",", s.jobs.Select(j => j.status)));
            Check(s.notice.Contains("止めました"), "止めた理由を出す：" + s.notice);
            Check(sounds.SequenceEqual(new[] { true }), "止まったら「確認して」の音");
            Check(AvatarUploader.CannotStart() == null, "続きから始められる");
        }

        static void Declined(UploadSession s)
        {
            var d = s.jobs.Find(j => j.blueprintId == "avtr_3");
            Check(d.status == "skipped" && d.message.Contains("同意"), "同意しなかったものは飛ばす：" + d.status);
            var p = s.jobs.Find(j => j.blueprintId == "avtr_4");
            Check(p.status == "failed" && p.message.Contains("見つかりません"), "準備に失敗したものは失敗：" + p.message);
            Check(!fake.events.Contains("upload:avtr_3") && !fake.events.Contains("upload:avtr_4"), "飛ばしたものはアップロードしない");
            Check(Count(s, "done") == 3, "残りの 3 体は完了");
        }

        static void Unchanged(UploadSession s)
        {
            var u = s.jobs.Find(j => j.blueprintId == "avtr_1");
            Check(u.status == "done" && u.message.Contains("省かれました"), "中身が同じでアップロードが省かれたときも完了：" + u.message);
            Check(sounds.SequenceEqual(new[] { false }), "失敗が無ければ「終わった」の音");
        }

        static void NotLoggedIn(UploadSession s)
        {
            Check(s.notice.Contains("ログイン"), "ログインしていないと理由を出して止まる：" + s.notice);
            Check(s.jobs.All(j => j.status == "waiting"), "行は待機中のまま");
            Check(!fake.events.Any(e => e.StartsWith("upload:")), "アップロードはしない");
            Check(fake.events.Contains("open"), "SDK の画面を開こうとする");
            Check(sounds.SequenceEqual(new[] { true }), "止まったら「確認して」の音");
            AvatarUploader.WaitSeconds = 20;
        }

        static void SlowScenario(string name, bool abort)
        {
            Scenario(name, () => fake.slow.Add("avtr_2"), s => { }, s => { });
            // Scenario の「終わるのを待つ」より前に止めたいので、組み直す
            var list = steps.ToList();
            steps.Clear();
            foreach (var x in list.Take(list.Count - 2)) steps.Enqueue(x);
            Wait(name + "：2 体目のビルドを待つ", () => fake.events.Contains("upload:avtr_2"));
            Do(name + "：止める", () =>
            {
                if (abort) AvatarUploader.Abort();
                else
                {
                    AvatarUploader.StopAfterCurrent();
                    fake.slow.Remove("avtr_2");
                }
            });
            Wait(name + "：止まるのを待つ", () => AvatarUploader.Current.IsCompleted && !AvatarUploader.Working);
            Do(name + "：確認", () =>
            {
                var s = AvatarUploader.Session;
                var second = s.jobs.Find(j => j.blueprintId == "avtr_2");
                if (abort)
                {
                    Check(second.status == "cancelled", "アップロード中のものは中止：" + second.status);
                    Check(fake.cancelled, "SDK のアップロードも取り消す");
                }
                else Check(second.status == "done", "いまのは最後まで終わる：" + second.status);
                Check(s.jobs.Skip(2).All(j => j.status == "waiting"), "残りは待機中：" + string.Join(",", s.jobs.Select(j => j.status)));
                Check(!s.running && sounds.Count == 0, "自分で止めたときは音を鳴らさない");
                if (!abort) return;
                fake.cancelled = false;
                fake.slow.Clear();
                fake.events.Clear();
                Check(AvatarUploader.CannotStart() == null, "続きから始められる");
                AvatarUploader.Start();
            });
            if (!abort) return;
            Wait(name + "：続きが終わるのを待つ", () => AvatarUploader.Current.IsCompleted && !AvatarUploader.Working);
            Do(name + "：続きの確認", () =>
            {
                var s = AvatarUploader.Session;
                Check(Count(s, "done") == 5, "中止したものと残りがアップロードされる：" + string.Join(",", s.jobs.Select(j => j.status)));
                Check(!fake.events.Contains("upload:avtr_1"), "完了したものはもう一度アップロードしない");
            });
        }

        static void HiddenSteps()
        {
            Do("非表示：準備", () =>
            {
                fake = new Fake();
                AvatarUploader.Backend = fake;
                MakeScene();
                fake.ids[avatars[0]] = "avtr_hidden";
                root.SetActive(false);
                var s = AvatarUploader.Session;
                s.jobs.Clear();
                AvatarUploader.Add(new[] { avatars[0] }, new List<string>());
                Check(s.jobs.Count == 1, "非表示（親が非表示）のアバターも追加できる");
                AvatarUploader.Start();
            });
            Wait("非表示：終わるのを待つ", () => AvatarUploader.Current.IsCompleted && !AvatarUploader.Working);
            Do("非表示：確認", () =>
            {
                var j = AvatarUploader.Session.jobs[0];
                Check(j.status == "done", "非表示のアバターもアップロードする：" + j.status + " " + j.message);
                Check(fake.activeAtUpload.TryGetValue("avtr_hidden", out var on) && on, "アップロードの間だけ表示にする");
                Check(!root.activeSelf && avatars[0].activeSelf, "終わったら非表示に戻す");
                Check(j.message.Contains("非表示"), "表示にしたことを行に書く：" + j.message);
            });
        }

        static void Recover()
        {
            var s = AvatarUploader.Session;
            s.jobs.Clear();
            s.jobs.Add(new UploadJob { id = "r1", name = "AvatarR1", blueprintId = "avtr_r1", status = "uploading", phase = "アップロード中", progress = 0.4f });
            s.jobs.Add(new UploadJob { id = "r2", name = "AvatarR2", blueprintId = "avtr_r2", status = "ready" });
            s.jobs.Add(new UploadJob { id = "r3", name = "AvatarR3", blueprintId = "avtr_r3", status = "done" });
            s.running = true;
            AvatarUploader.Save();
            AvatarUploader.ResetCache();
            sounds.Clear();
            typeof(AvatarUploader).GetMethod("RecoverAfterReload", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            s = AvatarUploader.Session;
            Check(s.Find("r1").status == "interrupted" && s.Find("r1").message.Contains("中断"), "アップロード中だった行は「中断」にする");
            Check(s.Find("r2").status == "waiting", "順番待ちだった行は待機中に戻す");
            Check(s.Find("r3").status == "done", "終わった行はそのまま");
            Check(!s.running && s.notice.Contains("再読み込み"), "止まった理由を出す：" + s.notice);
            Check(sounds.SequenceEqual(new[] { true }), "止まったら「確認して」の音");
            Check(s.Find("r1").Runnable && AvatarUploader.CannotStart() == null, "中断した行は「続きを開始」でやり直せる");
        }

        static void Persist()
        {
            var s = AvatarUploader.Session;
            s.jobs.Clear();
            s.jobs.Add(new UploadJob { id = "p1", name = "Avatar P1", scenePath = "Assets/A.unity", objectPath = "Root/Avatar P1", blueprintId = "avtr_p1", status = "done", seconds = 83, finishedAt = "12:34" });
            s.jobs.Add(new UploadJob { id = "p2", name = "アバター２", blueprintId = "avtr_p2", status = "failed", message = "SDK のチェックでエラー" });
            s.stopOnError = true;
            AvatarUploader.Save();
            Check(File.Exists(Path.Combine(work, "lib", "upload-queue.json")), "キューを Library/VIAH（ここではテスト用の場所）に保存");
            AvatarUploader.ResetCache();
            var t = AvatarUploader.Session;
            Check(t.jobs.Count == 2 && t.stopOnError, "読み直しても同じ");
            Check(t.Find("p1").objectPath == "Root/Avatar P1" && t.Find("p1").seconds == 83 && t.Find("p1").SceneName == "A", "行の中身も同じ");
            Check(t.Find("p2").name == "アバター２" && t.Find("p2").SceneName == "保存していないシーン", "全角の名前も読める");
            t.stopOnError = false;
            t.jobs.Clear();
            AvatarUploader.Save();
        }

        static void NoSdk()
        {
            AvatarUploader.Backend = null;
            Check((AvatarUploader.CannotStart() ?? "").Contains("VRChat SDK"), "SDK が無いと始められない：" + AvatarUploader.CannotStart());
            var problems = new List<string>();
            Check(AvatarUploader.Add(new[] { root }, problems) == 0 && problems.Any(p => p.Contains("入っていません")), "SDK が無いと追加もできない");
            Check(AvatarUploader.LoadedAvatars().Count == 0, "SDK が無いとアバターを探さない");
            AvatarUploader.Backend = fake;
        }

        static string PathA => SceneDir + "/A.unity";
        static string PathB => SceneDir + "/B.unity";
        static string PathD => SceneDir + "/D.unity";
        static string[] layoutBefore;
        static string hashA, hashB, hashD, activeBefore;

        static string Hash(string path)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create()) return BitConverter.ToString(md5.ComputeHash(File.ReadAllBytes(path)));
        }

        static string[] Layout() => Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
            .Select(x => $"{x.path}|{x.isLoaded}").ToArray();

        static Scene NewSaved(string path, params GameObject[] objects)
        {
            if (!AssetDatabase.IsValidFolder(SceneDir)) AssetDatabase.CreateFolder("Assets", "VIAH_UploadTest");
            var sc = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            foreach (var o in objects) SceneManager.MoveGameObjectToScene(o, sc);
            EditorSceneManager.SaveScene(sc, path);
            return sc;
        }

        static void CloseAll()
        {
            if (testScene.IsValid() && testScene.isLoaded && testScene.path == "") EditorSceneManager.CloseScene(testScene, true);
            testScene = default;
            foreach (var p in new[] { PathA, PathB, PathD })
            {
                var sc = SceneManager.GetSceneByPath(p);
                if (sc.IsValid() && SceneManager.sceneCount > 1) EditorSceneManager.CloseScene(sc, true);
            }
        }

        static GameObject Root(string path, string name) =>
            SceneManager.GetSceneByPath(path).GetRootGameObjects().FirstOrDefault(g => g.name == name);

        static void NewFake()
        {
            fake = new Fake();
            AvatarUploader.Backend = fake;
            fake.idsByName["AvatarSceneA"] = "avtr_sa";
            fake.idsByName["AvatarSceneB"] = "avtr_sb";
            fake.idsByName["AvatarSceneBCopy"] = "avtr_sb";
            fake.idsByName["AvatarSceneD"] = "avtr_sd";
            sounds.Clear();
        }

        static void WaitRun(string name) => Wait(name + "：終わるのを待つ", () => AvatarUploader.Current.IsCompleted && !AvatarUploader.Working);

        static void SceneSwitchSteps()
        {
            // ── 閉じているシーン：追加で開いて、保存せずに閉じる。開いているシーンには触らない ──
            Do("別のシーン：準備", () =>
            {
                CloseAll();
                NewFake();
                var avA = new GameObject("AvatarSceneA");
                // 同じ ID のコピーが、開いている別のシーンにある
                var a = NewSaved(PathA, avA, new GameObject("AvatarSceneBCopy"));
                var avB = new GameObject("AvatarSceneB");
                avB.SetActive(false);
                var b = NewSaved(PathB, avB);
                var avD = new GameObject("AvatarSceneD");
                var d = NewSaved(PathD, avD);
                var s = AvatarUploader.Session;
                s.jobs.Clear();
                s.stopOnError = false;
                AvatarUploader.Add(new[] { avA, avB, avD }, new List<string>());
                Check(s.jobs.Count == 3 && s.jobs[1].scenePath == PathB, "シーンの場所を覚える");
                EditorSceneManager.CloseScene(b, true);
                // 並んでいるが読み込んでいないシーン
                EditorSceneManager.CloseScene(d, false);
                // 保存していない変更
                SceneManager.MoveGameObjectToScene(new GameObject("UnsavedEdit"), a);
                EditorSceneManager.MarkSceneDirty(a);
                layoutBefore = Layout();
                activeBefore = SceneManager.GetActiveScene().path;
                hashA = Hash(PathA);
                hashB = Hash(PathB);
                hashD = Hash(PathD);
                Check(AvatarUploader.Find(s.jobs[1]) == null, "同じ ID のコピーが開いていても、登録したシーンが閉じていれば取り違えない");
                AvatarUploader.Start();
            });
            WaitRun("別のシーン");
            Do("別のシーン：確認", () =>
            {
                var s = AvatarUploader.Session;
                Check(s.jobs.All(j => j.status == "done"), "閉じているシーンのアバターもアップロード：" + string.Join(",", s.jobs.Select(j => j.status + " " + j.message)));
                Check(fake.sceneAtUpload.TryGetValue("avtr_sb", out var from) && from == PathB, "コピーではなく、登録したシーンのアバターを上げる：" + from);
                Check(fake.sceneAtUpload.TryGetValue("avtr_sd", out var fromD) && fromD == PathD, "並んでいるが読み込んでいないシーンも読み込んで上げる");
                Check(fake.activeAtUpload.TryGetValue("avtr_sb", out var on) && on, "閉じているシーンの非表示のアバターも、アップロードの間は表示");
                Check(Layout().SequenceEqual(layoutBefore), "シーンの並びと読み込みの状態が元どおり：" + string.Join(" / ", Layout()));
                Check(SceneManager.GetActiveScene().path == activeBefore, "アクティブなシーンは変わらない");
                var a = SceneManager.GetSceneByPath(PathA);
                Check(a.isLoaded && a.isDirty && Root(PathA, "UnsavedEdit") != null, "開いているシーンの保存していない変更が残る（捨てない）");
                Check(Hash(PathA) == hashA, "開いているシーンを勝手に保存しない");
                Check(Hash(PathB) == hashB && Hash(PathD) == hashD, "追加で開いたシーンを保存しない");
                Check(s.openedScene == "", "追加で開いたシーンの記録が残らない");
            });

            // ── SDK がビルドの中でシーンを保存したとき ──
            Do("SDK がシーンを保存：準備", () =>
            {
                NewFake();
                fake.saveSceneDuringUpload = true;
                var s = AvatarUploader.Session;
                s.jobs.Clear();
                s.jobs.Add(new UploadJob { id = "sb", name = "AvatarSceneB", scenePath = PathB, objectPath = "AvatarSceneB", blueprintId = "avtr_sb" });
                AvatarUploader.Start();
            });
            WaitRun("SDK がシーンを保存");
            Do("SDK がシーンを保存：確認", () =>
            {
                Check(AvatarUploader.Session.jobs[0].status == "done", "アップロードは完了");
                EditorSceneManager.OpenScene(PathB, OpenSceneMode.Additive);
                var av = Root(PathB, "AvatarSceneB");
                Check(av != null && !av.activeSelf, "表示にしたままの状態がシーンのファイルに残らない");
                EditorSceneManager.CloseScene(SceneManager.GetSceneByPath(PathB), true);
            });

            // ── 同じシーンに同じ ID が複数 ──
            Do("同じ ID が複数：準備", () =>
            {
                NewFake();
                fake.idsByName["AvatarSceneA"] = "avtr_sb";
                var s = AvatarUploader.Session;
                s.jobs.Clear();
                s.jobs.Add(new UploadJob { id = "dup", name = "AvatarX", scenePath = PathA, objectPath = "AvatarX", blueprintId = "avtr_sb" });
                AvatarUploader.Start();
            });
            WaitRun("同じ ID が複数");
            Do("同じ ID が複数：確認", () =>
            {
                var j = AvatarUploader.Session.jobs[0];
                Check(j.status == "failed" && j.message.Contains("同じアバター ID"), "決められないときは上げずに理由を出す：" + j.message);
                Check(!fake.events.Any(e => e.StartsWith("upload:")), "間違ったアバターを上げない");
            });

            // ── 保存していない新しいシーンを開いているとき ──
            Do("新しいシーンを開いたまま：準備", () =>
            {
                CloseAll();
                MakeScene();
                NewFake();
                var s = AvatarUploader.Session;
                s.jobs.Clear();
                s.jobs.Add(new UploadJob { id = "sb", name = "AvatarSceneB", scenePath = PathB, objectPath = "AvatarSceneB", blueprintId = "avtr_sb" });
                layoutBefore = Layout();
                AvatarUploader.Start();
            });
            WaitRun("新しいシーンを開いたまま");
            Do("新しいシーンを開いたまま：確認", () =>
            {
                var j = AvatarUploader.Session.jobs[0];
                Check(j.status == "done", "保存していない新しいシーンがあっても上げられる：" + j.status + " " + j.message);
                Check(Layout().SequenceEqual(layoutBefore) && root != null, "新しいシーンは開いたまま");
            });

            // ── 再読み込みのあと：開いたシーンと表示を戻す ──
            Do("再読み込みのあとの片付け", () =>
            {
                CloseAll();
                NewFake();
                EditorSceneManager.OpenScene(PathA, OpenSceneMode.Additive);
                EditorSceneManager.OpenScene(PathB, OpenSceneMode.Additive);
                Root(PathB, "AvatarSceneB").SetActive(true);
                var avA = Root(PathA, "AvatarSceneA");
                avA.SetActive(true);
                var s = AvatarUploader.Session;
                s.jobs.Clear();
                var job = new UploadJob { id = "ra", name = "AvatarSceneA", scenePath = PathA, objectPath = "AvatarSceneA", blueprintId = "avtr_sa", status = "building" };
                job.shown.Add("AvatarSceneA");
                s.jobs.Add(job);
                s.running = true;
                s.openedScene = PathB;
                s.openedSceneListed = false;
                AvatarUploader.Save();
                AvatarUploader.ResetCache();
                typeof(AvatarUploader).GetMethod("RecoverAfterReload", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
                Check(!avA.activeSelf, "アップロードの間だけ表示にしていたものを非表示に戻す");
                Check(!SceneManager.GetSceneByPath(PathB).IsValid(), "アップロードのために開いたシーンを閉じる");
                Check(SceneManager.GetSceneByPath(PathA).isLoaded, "ほかのシーンは閉じない");
                CloseAll();
            });

            // ── 取り消しが別の例外で来る ──
            Do("取り消しが別の例外：準備", () =>
            {
                fake = new Fake { cancelAsError = true };
                AvatarUploader.Backend = fake;
                MakeScene();
                for (int i = 0; i < avatars.Count; i++) fake.ids[avatars[i]] = "avtr_" + (i + 1);
                fake.slow.Add("avtr_1");
                var s = AvatarUploader.Session;
                s.jobs.Clear();
                AvatarUploader.Add(new[] { root }, new List<string>());
                AvatarUploader.Start();
            });
            Wait("取り消しが別の例外：1 体目を待つ", () => fake.events.Contains("upload:avtr_1"));
            Do("取り消しが別の例外：中止", AvatarUploader.Abort);
            WaitRun("取り消しが別の例外");
            Do("取り消しが別の例外：確認", () =>
            {
                var j = AvatarUploader.Session.jobs[0];
                Check(j.status == "cancelled", "SDK が別の例外で知らせても「中止」にする：" + j.status + " " + j.message);
            });

            // ── やり直しと Undo ──
            Do("やり直しと Undo", () =>
            {
                var page = new VInc.AvatarHub.UI.UploaderPage();
                var type = typeof(VInc.AvatarHub.UI.UploaderPage);
                var oldPage = UndoHub.CurrentPage;
                UndoHub.CurrentPage = page;
                var s = AvatarUploader.Session;
                s.running = false;
                s.jobs.Clear();
                s.jobs.Add(new UploadJob { id = "u1", name = "A1", blueprintId = "avtr_u1", status = "failed", message = "エラー", runs = 1 });
                s.jobs.Add(new UploadJob { id = "u2", name = "A2", blueprintId = "avtr_u2", status = "skipped", message = "同意しなかった", runs = 1 });
                s.jobs.Add(new UploadJob { id = "u3", name = "A3", blueprintId = "avtr_u3", status = "done", runs = 1 });
                page.Committed = page.CaptureState();
                type.GetMethod("RetryFailed", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(page, null);
                Check(s.Find("u1").status == "waiting" && s.Find("u2").status == "waiting", "失敗したものと、同意しなかったものを待機中に戻す");
                Check(s.Find("u3").status == "done", "完了したものはそのまま");
                Undo.PerformUndo();
                s = AvatarUploader.Session;
                Check(s.Find("u1").status == "failed" && s.Find("u2").status == "skipped" && s.Find("u1").message == "エラー", "「失敗したものをもう一度」を Ctrl+Z で戻せる：" + string.Join(",", s.jobs.Select(j => j.status)));
                Undo.PerformRedo();
                Check(AvatarUploader.Session.Find("u1").status == "waiting", "Ctrl+Y でやり直せる");

                // 実行したあとに消して Undo：実行前の「待機中」には戻らない
                s = AvatarUploader.Session;
                s.jobs.Clear();
                s.jobs.Add(new UploadJob { id = "v1", name = "B1", blueprintId = "avtr_v1" });
                page.Committed = page.CaptureState();
                s.jobs[0].status = "done";
                s.jobs[0].runs = 1;
                var edit = type.GetMethod("Edit", BindingFlags.NonPublic | BindingFlags.Instance);
                edit.Invoke(page, new object[] { "消す", (Action)(() => AvatarUploader.Session.jobs.Clear()) });
                Undo.PerformUndo();
                Check(AvatarUploader.Session.jobs.Count == 1 && AvatarUploader.Session.jobs[0].status == "done", "完了した行を消して戻しても「完了」のまま（二重にアップロードしない）");

                // 並べ替えたあとに実行して Undo：結果は残る
                s = AvatarUploader.Session;
                s.jobs.Clear();
                s.jobs.Add(new UploadJob { id = "w1", name = "C1", blueprintId = "avtr_w1" });
                s.jobs.Add(new UploadJob { id = "w2", name = "C2", blueprintId = "avtr_w2" });
                page.Committed = page.CaptureState();
                edit.Invoke(page, new object[] { "並べ替え", (Action)(() => AvatarUploader.Session.jobs.Reverse()) });
                foreach (var j in AvatarUploader.Session.jobs)
                {
                    j.status = "done";
                    j.runs++;
                }
                Undo.PerformUndo();
                s = AvatarUploader.Session;
                Check(s.jobs[0].id == "w1" && s.jobs.All(j => j.status == "done"), "並べ替えを戻しても、実行した結果は残る");
                UndoHub.CurrentPage = oldPage;
                s.jobs.Clear();
                AvatarUploader.Save();
            });

            // ── 画面の組み立て（ウィンドウは開かない） ──
            Do("画面の組み立て", () =>
            {
                NewFake();
                var s = AvatarUploader.Session;
                s.running = false;
                s.jobs.Clear();
                Check(Texts(BuildPage()).Any(t => t.Contains("まだありません")), "空のキュー");
                s.jobs.Add(new UploadJob { id = "p1", name = "P1", blueprintId = "avtr_p1", status = "done" });
                s.jobs.Add(new UploadJob { id = "p2", name = "P2", blueprintId = "avtr_p2", status = "skipped", message = "著作権の同意をしなかったので飛ばしました" });
                var card = BuildPage().root.Q("card-upload-result");
                Check(card != null && Texts(card).Any(t => t == "失敗したものをもう一度"), "同意しなかったものだけでも、結果にやり直しのボタンを出す");
                s.jobs[1].status = "uploading";
                s.jobs[1].phase = "アップロード中";
                s.jobs[1].progress = 0.4f;
                s.running = true;
                Check(BuildPage().root.Q("btn-upload-stop") != null, "実行中は「止める」を出す");
                s.running = false;
                s.jobs.Clear();
                AvatarUploader.Backend = null;
                Check(Texts(BuildPage()).Any(t => t.Contains("3.10.6")), "SDK が無いときは必要な版を出す");
                AvatarUploader.Backend = fake;
                int topics = 0;
                foreach (var t in VInc.AvatarHub.UI.GuideContent.Topics.Where(x => x.group == "uploader"))
                {
                    Check(t.intro.Length + t.steps.Count > 0, "ガイド：" + t.title);
                    topics++;
                }
                Check(topics == 3, "アップローダーのガイドは 3 つ");
            });
        }

        static VInc.AvatarHub.UI.UploaderPage BuildPage()
        {
            var page = new VInc.AvatarHub.UI.UploaderPage();
            page.Build();
            page.OnHide();
            return page;
        }

        static List<string> Texts(VInc.AvatarHub.UI.UploaderPage page) => Texts(page.root);

        static List<string> Texts(VisualElement e) =>
            e.Query<Label>().ToList().Select(l => l.text).Concat(e.Query<Button>().ToList().Select(b => b.text)).Where(t => !string.IsNullOrEmpty(t)).ToList();
    }
}
