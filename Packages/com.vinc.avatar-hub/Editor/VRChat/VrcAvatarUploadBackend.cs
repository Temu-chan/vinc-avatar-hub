using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDK3A.Editor;
using VRC.SDKBase;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;

namespace VInc.AvatarHub.VRChat
{
    /// <summary>
    /// ⑦ アバターアップローダーの、VRChat SDK を呼ぶ部分。VRChat SDK（Avatars 3.10.6 以降）が入っているときだけコンパイルされる。
    /// アップロードは SDK の公開 API（IVRCSdkAvatarBuilderApi.BuildAndUpload）で行うので、SDK の画面が開いている必要がある。
    /// </summary>
    [InitializeOnLoad]
    class VrcAvatarUploadBackend : IAvatarUploadBackend
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>準備のときに取ってきた VRChat 上のアバターの情報（アップロードで使う）。</summary>
        readonly Dictionary<string, VRCAvatar> fetched = new Dictionary<string, VRCAvatar>();

        static VrcAvatarUploadBackend()
        {
            AvatarUploader.Backend = new VrcAvatarUploadBackend();
        }

        public string SdkVersion => UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(IVRCSdkAvatarBuilderApi).Assembly)?.version ?? "";

        public List<GameObject> FindAvatars(GameObject root)
        {
            if (root == null) return new List<GameObject>();
            return root.GetComponentsInChildren<VRC_AvatarDescriptor>(true).Select(d => d.gameObject).Distinct().ToList();
        }

        public string BlueprintId(GameObject avatar) =>
            avatar != null && avatar.TryGetComponent<PipelineManager>(out var pm) ? pm.blueprintId ?? "" : "";

        public bool PanelOpen => VRCSdkControlPanel.window != null;

        public bool LoggedIn => APIUser.IsLoggedIn && APIUser.CurrentUser != null;

        public string UserName => APIUser.CurrentUser?.displayName ?? "";

        public string Platform
        {
            get
            {
                switch (EditorUserBuildSettings.activeBuildTarget)
                {
                    case BuildTarget.Android: return "Android（Quest）";
                    case BuildTarget.iOS: return "iOS";
                    default: return "Windows（PC）";
                }
            }
        }

        public void OpenPanel()
        {
            if (!PanelOpen) EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
            var w = VRCSdkControlPanel.window;
            if (w == null) return;
            w.Show();
            // ほかのタブに隠れていることがあるので前に出す
            w.Focus();
            if (LoggedIn) ShowBuilderTab(w);
        }

        /// <summary>進み具合が見えるように、SDK の画面を Builder タブにする（SDK の中の機能なので、できなければ何もしない）。</summary>
        static void ShowBuilderTab(VRCSdkControlPanel w)
        {
            try
            {
                var m = typeof(VRCSdkControlPanel).GetMethod("SelectTab", Any);
                var p = m?.GetParameters();
                if (p == null || p.Length != 1 || !p[0].ParameterType.IsEnum) return;
                m.Invoke(w, new[] { Enum.ToObject(p[0].ParameterType, 1) });
            }
            catch (Exception e)
            {
                Debug.Log("[Avatar Hub] SDK の画面を Builder タブにできませんでした: " + e.Message);
            }
        }

        public async Task<string> Prepare(string blueprintId, CancellationToken ct)
        {
            VRCAvatar a;
            try
            {
                a = await VRCApi.GetAvatar(blueprintId, true, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ApiErrorException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                throw new AvatarUploadException("VRChat にこのアバターが見つかりません（削除されたか、別のアカウントで作ったものです）", "", e);
            }
            catch (ApiErrorException e)
            {
                throw new AvatarUploadException("VRChat からアバターの情報を取れませんでした：" + (e.ErrorMessage ?? e.Message), "", e);
            }
            catch (Exception e)
            {
                throw new AvatarUploadException("VRChat からアバターの情報を取れませんでした：" + e.Message, "", e);
            }
            if (string.IsNullOrEmpty(a.ID))
                throw new AvatarUploadException("VRChat にこのアバターが見つかりません（削除されたか、別のアカウントで作ったものです）");
            var me = APIUser.CurrentUser?.id;
            if (!string.IsNullOrEmpty(me) && !string.IsNullOrEmpty(a.AuthorId) && a.AuthorId != me)
                throw new AvatarUploadException("いまログインしているアカウントのアバターではありません");
            fetched[blueprintId] = a;
            return a.Name;
        }

        public async Task Agree(string blueprintId, CancellationToken ct)
        {
            if (!fetched.TryGetValue(blueprintId, out var avatar)) throw new AvatarUploadException("アバターの情報がありません（準備に失敗しています）");
            // 同意の確認に使うのは Pipeline Manager の Blueprint ID だけなので、シーンを開かなくてすむように仮のものを作る
            var temp = EditorUtility.CreateGameObjectWithHideFlags("VIAH_CopyrightCheck", HideFlags.HideAndDontSave);
            try
            {
                var pm = temp.AddComponent<PipelineManager>();
                pm.blueprintId = blueprintId;
                var task = VRCCopyrightAgreement.CheckCopyrightAgreement(pm, avatar);
                var cancel = Task.Delay(Timeout.Infinite, ct);
                if (await Task.WhenAny(task, cancel) == cancel) throw new OperationCanceledException(ct);
                await task;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AvatarUploadException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw Translate(e, true);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temp);
            }
        }

        public async Task Upload(GameObject avatar, Action<string, float, bool> report, CancellationToken ct)
        {
            if (!PanelOpen) throw new AvatarUploadException("VRChat SDK の画面が閉じられました");
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder) || builder == null)
                throw new AvatarUploadException("VRChat SDK の Builder が使えません。SDK の画面でログインしているか確認してください");
            var id = BlueprintId(avatar);
            if (string.IsNullOrEmpty(id)) throw new AvatarUploadException("アバター ID（Pipeline Manager の Blueprint ID）がありません");
            if (!fetched.TryGetValue(id, out var info))
            {
                await Prepare(id, ct);
                info = fetched[id];
            }

            void OnBuildStart(object sender, object e) => report("ビルド中", -1, false);
            void OnBuildProgress(object sender, string message) => report(string.IsNullOrEmpty(message) ? "ビルド中" : $"ビルド中：{message}", -1, false);
            void OnUploadStart(object sender, EventArgs e) => report("アップロード中", 0, true);
            void OnUploadProgress(object sender, (string status, float percentage) e) => report("アップロード中", Mathf.Clamp01(e.percentage), true);
            builder.OnSdkBuildStart += OnBuildStart;
            builder.OnSdkBuildProgress += OnBuildProgress;
            builder.OnSdkUploadStart += OnUploadStart;
            builder.OnSdkUploadProgress += OnUploadProgress;
            // SDK は、アップロードしなかったとき（Dry Run の設定・通信切れ）も例外を出さずに終わることがある。
            // 完了の通知が来たときだけ「完了」にする
            bool succeeded = false;
            void OnUploadSuccess(object sender, string avatarId) => succeeded = true;
            builder.OnSdkUploadSuccess += OnUploadSuccess;
            try
            {
                // SDK の画面にも、いまのアバターを出す
                try { builder.SelectAvatar(avatar); }
                catch (Exception e) { Debug.Log("[Avatar Hub] SDK の画面でアバターを選べませんでした: " + e.Message); }
                report("ビルドの準備中", -1, false);
                // SDK の「Build & Publish」と同じく、プラットフォームごとの差し替え（Per-Platform Overrides）も使う
                await builder.BuildAndUpload(avatar, PerPlatformOverrides.GetPlatformOverrides(avatar), info, null, ct);
                if (!succeeded)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new AvatarUploadException("SDK からアップロード完了の通知がありませんでした。SDK の設定（Settings）の Dry Run Builds がオンになっていないか、通信が切れていないか確認してください");
                }
            }
            catch (Exception e) when (succeeded)
            {
                // アップロードは終わっている（そのあとの片付けでのエラー）
                Debug.LogWarning("[Avatar Hub] アップロードのあとで SDK がエラーを出しました: " + e.Message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (Exception e)
            {
                throw Translate(e);
            }
            finally
            {
                builder.OnSdkBuildStart -= OnBuildStart;
                builder.OnSdkBuildProgress -= OnBuildProgress;
                builder.OnSdkUploadStart -= OnUploadStart;
                builder.OnSdkUploadProgress -= OnUploadProgress;
                builder.OnSdkUploadSuccess -= OnUploadSuccess;
            }
        }

        public void CancelUpload()
        {
            if (!PanelOpen) return;
            if (VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder) && builder != null) builder.CancelUpload();
        }

        /// <summary>SDK の例外を、画面に出す説明に変える。agreeing は著作権の同意を確かめているとき。</summary>
        static Exception Translate(Exception e, bool agreeing = false)
        {
            if (e is AggregateException a && a.InnerExceptions.Count == 1) e = a.InnerExceptions[0];
            switch (e)
            {
                case OperationCanceledException _:
                    return e;
                case AvatarUploadException _:
                    return e;
                case ValidationException v:
                    var errors = v.Errors != null && v.Errors.Count > 0 ? string.Join(" / ", v.Errors.Take(3)) : v.Message;
                    return new AvatarUploadException($"SDK のチェックでエラーがあります：{errors}（SDK の画面で内容を確認してください）", "", e);
                case OwnershipException _ when agreeing:
                    return new AvatarUploadException("著作権の同意をしなかったので飛ばしました", "declined", e);
                case OwnershipException _:
                    return new AvatarUploadException($"このアバターをアップロードできません（著作権の同意・持ち主の確認）：{e.Message}", "", e);
                case BuildBlockedException _:
                    return new AvatarUploadException($"ビルドが止められました（ほかのツールのチェックなど）：{e.Message}", "", e);
                case BundleExistsException _:
                    return new AvatarUploadException("前回と中身が同じなので、アップロードは省かれました", "unchanged", e);
                case BuilderException _:
                    return new AvatarUploadException($"ビルドに失敗しました：{e.Message}（Console のエラーを確認してください）", "", e);
                case UploadException _:
                    return new AvatarUploadException($"アップロードに失敗しました：{e.Message}", "", e);
                case ApiErrorException api:
                    return new AvatarUploadException($"VRChat のサーバーでエラーが起きました：{api.ErrorMessage ?? api.Message}", "", e);
                default:
                    return new AvatarUploadException($"{e.GetType().Name}：{e.Message}", "", e);
            }
        }
    }
}
