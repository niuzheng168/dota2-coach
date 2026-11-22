using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DotaCoach.TwoPc;

internal sealed partial class CoachWindow
{
    private readonly Func<HttpMessageHandler>? modelHandlerFactory;
    private readonly ComboBox modelProvider = Ui.Choice(), copilotModel = Ui.Choice();
    private readonly TextBox copilotBaseUrl = Ui.Input(), copilotKeyFile = Ui.Input();
    private readonly TextBlock catalogStatus = Ui.Text("", 13, Ui.Accent);
    private Button refreshModels = null!;
    private static readonly string[] CopilotIds = [CopilotGateway.Astra, CopilotGateway.Sol];
    private const string UncheckedCatalog = "尚未核实此代理提供哪些模型。请先刷新清单，再保存模型设置以启用推理。";
    private const string DraftHint = "模型设置尚未保存。旧请求已取消，旧标记已撤回；当前不会推理。";
    private string modelSetupHint = "请在「模型设置」中选择服务并保存。";
    private bool modelEditsPending;
    // Only IDs from our two-item allowlist. Neither the listing nor its authorization survives a restart.
    private string[]? listedModels;
    private CancellationTokenSource? catalogStop;
    private bool CopilotSelected => Equals(modelProvider.SelectedValue, CopilotGateway.ProviderId);
    private string ActiveModelLabel => preferences.ModelProvider switch {
        LiveCoachSettings.FoundryProvider => "Foundry · " + preferences.EffectiveModel,
        CopilotGateway.ProviderId => "Codey / Copilot · " + preferences.EffectiveModel,
        _ => "未选择模型服务"
    };

    private void BuildProviderControls(StackPanel settings)
    {
        modelProvider.SelectedValuePath = nameof(ComboBoxItem.Tag);
        modelProvider.Items.Add(new ComboBoxItem { Tag = LiveCoachSettings.FoundryProvider, Content = "Azure AI Foundry" });
        modelProvider.Items.Add(new ComboBoxItem { Tag = CopilotGateway.ProviderId, Content = "Codey · GitHub Copilot 本地代理" });
        modelProvider.SelectedValue = preferences.ModelProvider;
        settings.Children.Add(Ui.Label("模型服务")); settings.Children.Add(modelProvider);

        Button Browse(TextBox target, string title) => Ui.Button("选择…", (_, _) => {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = "配置文件|*.env;*.conf;*.txt|所有文件|*.*" };
            if (dialog.ShowDialog(this) == true) target.Text = dialog.FileName; // Path only; never preview the file.
        });
        var foundry = new StackPanel();
        foundryFile.Text = preferences.FoundryEnvFile; reasoningModel.Text = preferences.Model;
        foundry.Children.Add(Ui.Label("Foundry 配置文件（服务地址与密钥）"));
        foundry.Children.Add(Ui.Row(foundryFile, Browse(foundryFile, "选择 Foundry 配置文件")));
        foundry.Children.Add(Ui.Label("Foundry 部署名")); foundry.Children.Add(reasoningModel);
        settings.Children.Add(foundry);

        var copilot = new StackPanel();
        copilotBaseUrl.Text = preferences.CopilotBaseUrl; copilotKeyFile.Text = preferences.CopilotKeyFile;
        copilotModel.ItemsSource = CopilotIds; copilotModel.SelectedItem = preferences.CopilotModel;
        copilot.Children.Add(Ui.Label("Codey 本机模型 API 地址（仅回环，可改端口）")); copilot.Children.Add(copilotBaseUrl);
        copilot.Children.Add(Ui.Label("Copilot 模型 ID")); copilot.Children.Add(copilotModel);
        copilot.Children.Add(Ui.Label("Codey 专用密钥文件路径（可选；不要填写密钥本身）"));
        copilot.Children.Add(Ui.Row(copilotKeyFile, Browse(copilotKeyFile, "选择 Codey 本地模型 API 专用配置文件")));
        copilot.Children.Add(Ui.Text("留空使用教练机环境变量 CODEY_MODEL_API_KEY；专用文件只写 CODEY_MODEL_API_KEY=…。" +
            "不要选 OAuth 或 Foundry 文件。教练端不代登录、不自动换模型；只有代理在本机，数据可能转发到 Codey 配置的上游。", 12, Brushes.LightGray));
        refreshModels = Ui.Button("刷新可用模型（不推理）", async (_, _) => await RefreshCopilotModels());
        copilot.Children.Add(refreshModels); copilot.Children.Add(catalogStatus); settings.Children.Add(copilot);

        void ShowProvider() {
            foundry.Visibility = Equals(modelProvider.SelectedValue, LiveCoachSettings.FoundryProvider) ? Visibility.Visible : Visibility.Collapsed;
            copilot.Visibility = CopilotSelected ? Visibility.Visible : Visibility.Collapsed;
        }
        ShowProvider(); catalogStatus.Text = UncheckedCatalog;
        if (CopilotSelected) SetModelHint(UncheckedCatalog);
        modelProvider.SelectionChanged += (_, _) => { ShowProvider(); ModelEdited(forgetCatalog: true); };
        copilotBaseUrl.TextChanged += (_, _) => ModelEdited(forgetCatalog: true);
        copilotKeyFile.TextChanged += (_, _) => ModelEdited(forgetCatalog: true);
        copilotModel.SelectionChanged += (_, _) => ModelEdited();
        foreach (var box in new[] { foundryFile, reasoningModel, interval, requestLimit, subtitleSeconds, modelTimeout })
            box.TextChanged += (_, _) => ModelEdited();
    }

    private void SetModelHint(string text) => autoStatus.Text = modelSetupHint = text;
    private static string NotListed(string id) =>
        $"Codey 未列出可用的 {id}。请核对 Copilot 账户、路由和 Responses 支持，再刷新清单；不会改用其他模型。";

    // Display our own actions, never server bodies, arbitrary model IDs, URLs, paths, or exception messages.
    private static string ProviderHelp(Exception error, bool codey, bool vision)
    {
        var status = (error as ModelProviderException)?.StatusCode;
        if (status != null) return $"HTTP {status}：" + (status switch {
            401 => codey ? "本地模型密钥未通过验证；请检查 CODEY_MODEL_API_KEY 或专用密钥文件。" : "请检查 Foundry 配置文件中的服务地址与密钥。",
            403 => codey ? "访问被拒绝；请检查 Codey 的模型密钥权限、Copilot 账户和路由授权。" : "请检查 Foundry 部署和密钥的访问权限。",
            404 => codey ? "未找到接口或模型；请核对回环 /v1 地址、模型 ID 并刷新清单。" : "请检查 Foundry 部署名和服务地址。",
            429 => "请求受限或配额不足；请等待限流解除或检查账户配额后再试。",
            400 or 413 or 415 or 422 when vision => "图像请求被拒绝；请确认该路由支持 Responses 图像输入及当前画面大小。",
            _ => "请检查所选模型服务的状态和访问权限。"
        });
        if (error is OperationCanceledException) return "请求已取消或超时；请检查连接和超时设置。";
        if (codey && error is InvalidOperationException or InvalidDataException)
            return "代理未确认所选模型或有效答复。请刷新清单：若该 ID 未列出，检查 Copilot 路由和 Responses 支持；" +
                (vision ? "若仍列出，检查视觉支持、图像大小上限及返回的模型 ID。" : "若仍列出，检查返回的模型 ID 是否一致。");
        if (codey) return "请检查本机 /v1 地址、Codey 是否运行，以及专用模型密钥（CODEY_MODEL_API_KEY），然后刷新清单。";
        return "请检查 Foundry 配置和网络；答复无效或对局状态已变时也不会回传。";
    }

    private void ReportModelFailure(Exception error, bool vision, bool inModelRequest)
    {
        bool codey = e2e == null && preferences.ModelProvider == CopilotGateway.ProviderId;
        string hint = !inModelRequest && modelConfigured ? "对局状态已变或来源已过期；请等待新鲜的本人 GSI。" : ProviderHelp(error, codey, vision);
        // A preflight/response rejection can supersede an earlier successful list. An expired
        // local publication, on the other hand, says nothing about the provider's availability.
        bool needsRecheck = error switch {
            ModelProviderException p => p.StatusCode is 401 or 403 or 404 or 429 || vision && p.StatusCode is 400 or 413 or 415 or 422,
            InvalidOperationException or InvalidDataException or HttpRequestException => true, _ => false
        };
        if (codey && inModelRequest && needsRecheck) {
            CancelCatalog(); SuspendLiveModel(); listedModels = null;
            SetModelHint(catalogStatus.Text = hint + " 已暂停；请重新刷新清单并保存。");
        } else if (e2e == null && !modelConfigured) SetModelHint("模型配置未能载入。" + hint);
        visionText.Text = $"{e2e?.Model ?? ActiveModelLabel} · {hint} 未回传标记，不自动换模型。";
    }
    private void SuspendLiveModel()
    {
        modelConfigured = false; CancelDecision(); host?.Mailbox.ClearModelReminder();
        publishedMinute = null; visionText.Text = "";
        cloud?.Dispose(); cloud = null; activeLayout = null;
        // Do not reset the shared request budget, the minute ledger, or retag a late answer.
    }
    private void CancelCatalog()
    {
        var pending = catalogStop; catalogStop = null;
        if (pending == null) return;
        pending.Cancel(); refreshModels.IsEnabled = true; catalogStatus.Text = UncheckedCatalog;
    }
    private void ModelEdited(bool forgetCatalog = false)
    {
        modelEditsPending = true; CancelCatalog(); SuspendLiveModel();
        if (forgetCatalog) { listedModels = null; catalogStatus.Text = UncheckedCatalog; }
        SetModelHint(DraftHint);
    }
    // Never use a hidden field as the other provider's endpoint, deployment, or credential.
    private LiveCoachSettings ProviderDraft() => modelProvider.SelectedValue switch {
        LiveCoachSettings.FoundryProvider => preferences with { ModelProvider = LiveCoachSettings.FoundryProvider,
            FoundryEnvFile = foundryFile.Text.Trim(), Model = reasoningModel.Text.Trim() },
        CopilotGateway.ProviderId => preferences with { ModelProvider = CopilotGateway.ProviderId,
            CopilotBaseUrl = copilotBaseUrl.Text.Trim(), CopilotModel = copilotModel.SelectedItem as string ?? "",
            CopilotKeyFile = copilotKeyFile.Text.Trim() },
        _ => throw new ArgumentException("请选择模型服务。")
    };

    private CloudCoach NewModel(LiveCoachSettings config, string lessons, CalibratedMap? map = null, MinimapLayout? layout = null) =>
        new(config, lessons, handler: modelHandlerFactory?.Invoke(), map: map, localBotOnly: false, layout: layout,
            timeout: TimeSpan.FromSeconds(config.ModelTimeoutSeconds));
    private CloudCoach CreateLiveModel(MinimapLayout? layout) => NewModel(preferences, liveLessons, liveMap, layout);

    private async Task RefreshCopilotModels()
    {
        if (closing || !CopilotSelected || catalogStop != null) return;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        // A catalog read is independent of pairing/disconnects and of inference's generation counter.
        // Conversely a late listing from a different address/key must never authorize this one.
        catalogStop = pending; refreshModels.IsEnabled = false; listedModels = null;
        SetModelHint(catalogStatus.Text = "正在读取 Codey 模型清单；本次刷新不发送游戏数据、不推理。");
        bool Current() => catalogStop == pending && !pending.IsCancellationRequested && !closing;
        try {
            var candidate = ProviderDraft();
            candidate.Validate();
            using var probe = NewModel(candidate, "");
            var ids = await probe.ListCopilotModelsAsync(pending.Token);
            if (!Current()) return;
            listedModels = CopilotIds.Where(id => ids.Contains(id, StringComparer.Ordinal)).ToArray();
            catalogStatus.Text = $"上次查询 {DateTime.Now:HH:mm:ss} · Copilot / Responses\n" +
                string.Join("\n", CopilotIds.Select(id => id + "：" + (listedModels.Contains(id) ? "代理已列出" : "未列出"))) +
                "\n清单不验证视觉能力，也不保证推理成功。";
            if (!listedModels.Contains(candidate.CopilotModel)) {
                SuspendLiveModel(); SetModelHint(NotListed(candidate.CopilotModel));
            } else SetModelHint($"Codey 已列出 {candidate.CopilotModel}。" + (modelConfigured ? "当前设置不变。" : "点「保存模型设置」应用；刷新本身不启用推理。"));
        } catch (Exception error) {
            if (!Current()) return;
            SuspendLiveModel();
            debug.Recorder?.Error("copilot-model-list", error);
            SetModelHint(catalogStatus.Text = "未能核实 Codey 模型清单。" + ProviderHelp(error, codey: true, vision: false) + " 未推理。");
        } finally {
            if (catalogStop == pending) { catalogStop = null; refreshModels.IsEnabled = true; }
        }
    }

    private void ConfigureModel(bool save)
    {
        bool wasActive = modelConfigured;
        CancelCatalog(); SuspendLiveModel();
        if (!save && modelEditsPending) { SetModelHint(DraftHint); return; }
        try {
            if (save) {
                if (modelProvider.SelectedValue == null) { SetModelHint("请选择模型服务再保存；不会自动切换。"); return; }
                if (!int.TryParse(interval.Text, out int seconds) || seconds is < 12 or > 300 ||
                    !int.TryParse(requestLimit.Text, out int limit) || limit is < 1 or > 1000 ||
                    !int.TryParse(subtitleSeconds.Text, out int display) || display < 1 || display > Wire.MaxReminderMs / 1000 ||
                    !int.TryParse(modelTimeout.Text, out int timeoutSeconds) || timeoutSeconds is < 8 or > 20) {
                    SetModelHint("请填写整数：间隔 12–300 秒、请求上限 1–1000、字幕 1–15 秒、超时 8–20 秒。未应用设置。"); return;
                }
                var candidate = ProviderDraft() with { IntervalSeconds = seconds, RequestLimit = limit,
                    SubtitleSeconds = display, ModelTimeoutSeconds = timeoutSeconds, BindAddress = (string)address.SelectedItem };
                candidate.Save(settingsPath); // Validates before replacing either the saved or in-memory profile.
                preferences = candidate; modelEditsPending = false;
            }
            if (preferences.ModelProvider == CopilotGateway.ProviderId) {
                string id = preferences.CopilotModel;
                if (listedModels == null) { SetModelHint(UncheckedCatalog); return; }
                if (!listedModels.Contains(id)) { SetModelHint(NotListed(id)); return; }
                if (!save && !wasActive) { SetModelHint($"Codey 已列出 {id}。请保存模型设置以启用推理。"); return; }
            }
            string data = System.IO.Path.Combine(AppContext.BaseDirectory, "data");
            // The minute mode never consumes the screenshot prompt, old map, or historical lessons.
            liveMap = preferences.MinuteFarm742 ? null : new CalibratedMap(System.IO.Path.Combine(data, "dota2-map-reference.json"));
            liveLessons = preferences.MinuteFarm742 ? "" : File.ReadAllText(System.IO.Path.Combine(data, "luna-live-lessons.json"));
            cloud = CreateLiveModel(null); modelConfigured = true;
            SetModelHint(ActiveModelLabel + (preferences.ModelProvider == CopilotGateway.ProviderId ? "：代理已列出该 ID。" : "：已载入配置，未验证远端部署。") +
                (preferences.MinuteFarm742 ? "等待本人 GSI、本局版本确认和小地图校准。" : "自动教练开启后，仅在收到新鲜自身 GSI 和真实游戏画面时推理。"));
        } catch (Exception error) {
            debug.Recorder?.Error("model-configuration", error);
            SetModelHint(CopilotSelected ? "Codey 设置未能应用：请检查回环 /v1 地址、所选 ID、独立的本地模型密钥及文件权限。未发出请求。" :
                "Foundry 设置未能应用：请检查配置文件、部署名及本机数据文件。未发出请求。");
        }
    }
}
