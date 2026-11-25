using CodexUsageWidget;

internal static class AlertPolicyVerification
{
    private static int checks;

    public static void Run()
    {
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var policy = new AlertPolicy(true, 30, 10, 0);
        Check(AlertTracker.Evaluate(new AlertState(), policy, 30, 100, now, false).Threshold == 0,
            "30% 경계에서는 경고 알림 없음");
        Check(AlertTracker.Evaluate(new AlertState(), policy, 29, 100, now, false).Threshold == 30,
            "30% 미만 경고 알림");
        Check(AlertTracker.Evaluate(new AlertState(), policy, 10, 100, now, false).Threshold == 30,
            "10% 경계에서는 경고 단계 유지");
        Check(AlertTracker.Evaluate(new AlertState(), policy, 9, 100, now, false).Threshold == 10,
            "10% 미만 위험 알림");

        var warned = new AlertState { ResetsAt = 100, Level = 1, LastAlertAtUnix = now.ToUnixTimeSeconds() };
        var repeatPolicy = policy with { RepeatMinutes = 10 };
        Check(AlertTracker.Evaluate(warned, repeatPolicy, 20, 100, now.AddMinutes(9), false).Threshold == 0,
            "반복 간격 이전에는 재알림 없음");
        Check(AlertTracker.Evaluate(warned, repeatPolicy, 20, 100, now.AddMinutes(10), false).Threshold == 30,
            "반복 간격 도달 시 재알림");

        var quietResult = AlertTracker.Evaluate(new AlertState(), policy, 20, 100, now, true);
        Check(quietResult.Threshold == 0 && quietResult.State.Level == 0, "방해 금지 중 알림 보류");
        Check(AlertTracker.Evaluate(quietResult.State, policy, 20, 100, now, false).Threshold == 30,
            "방해 금지 종료 후 보류 알림 전달");
        Check(AlertTracker.Evaluate(new AlertState(), policy with { Enabled = false }, 5, 100, now, false).Threshold == 0,
            "비활성 구간 알림 없음");

        var previousCycle = new AlertState { ResetsAt = 99, Level = 2, LastAlertAtUnix = now.AddDays(-1).ToUnixTimeSeconds() };
        Check(AlertTracker.Evaluate(previousCycle, policy, 5, 100, now, false).Threshold == 10,
            "새 리셋 주기에서 위험 알림 다시 허용");
        var recovered = AlertTracker.Evaluate(new AlertState { ResetsAt = 100, Level = 2 }, policy, 20, 100, now, false);
        Check(recovered.Threshold == 0 && recovered.State.Level == 1, "위험에서 경고로 회복하면 단계 낮춤");
        Check(AlertTracker.Evaluate(recovered.State, policy, 5, 100, now, false).Threshold == 10,
            "회복 후 다시 위험 구간에 들어가면 알림");

        var original = new AlertState { ResetsAt = 100, Level = 1 };
        AlertTracker.Evaluate(original, policy, 5, 100, now, false);
        Check(original.Level == 1, "정책 평가가 입력 알림 상태를 변경하지 않음");

        var normalized = new AlertPolicy(false, 120, 80, 9999).Normalize();
        Check(normalized.WarningPercent == 100 && normalized.CriticalPercent == 80 && normalized.RepeatMinutes == 1440,
            "정책 수치 범위 정규화");
        var settings = new AlertPolicySettings();
        settings.Set("codex:300", new AlertPolicy(true, 25, 5, 15));
        Check(settings.Get("codex:300").WarningPercent == 25 && settings.Get("go:weekly").WarningPercent == 30,
            "구간별 정책 조회와 기본값");

        const string nestedNullPolicyJson = """{"AlertPolicies":{"Policies":{"codex:300":null}}}""";
        var nestedNullSettings = System.Text.Json.JsonSerializer.Deserialize<AlertPolicySettingsDocument>(nestedNullPolicyJson)!.AlertPolicies;
        Check(nestedNullSettings.Policies["codex:300"] is null, "JSON의 중첩 null 정책 역직렬화");
        Check(nestedNullSettings.Get("codex:300") == AlertPolicy.Default,
            "JSON의 중첩 null 정책 조회 시 기본값 사용");
        nestedNullSettings.Normalize();
        Check(!nestedNullSettings.Policies.Values.Any(value => value is null),
            "JSON의 중첩 null 정책 정규화");
        Check(nestedNullSettings.Get("codex:300") == AlertPolicy.Default,
            "JSON의 중첩 null 정책 정규화 후 기본값 사용");

        var quiet = new AlertQuietHours { Enabled = true, StartHour = 22, EndHour = 8 };
        Check(quiet.IsQuiet(new DateTimeOffset(2026, 11, 26, 23, 0, 0, TimeSpan.Zero)), "자정을 넘는 방해 금지 시작");
        Check(quiet.IsQuiet(new DateTimeOffset(2026, 11, 27, 7, 0, 0, TimeSpan.Zero)), "자정을 넘는 방해 금지 종료");
        Check(!quiet.IsQuiet(now), "방해 금지 시간대 밖에는 알림 허용");

        var alerts = new Dictionary<string, AlertState> { ["codex:300"] = original };
        var changes = new[]
        {
            new AlertStateChange("codex:300", original, new AlertState { ResetsAt = 100, Level = 2 }),
            new AlertStateChange("go:weekly", null, new AlertState { ResetsAt = 200, Level = 1 })
        };
        Check(!AlertStateTransaction.Apply(alerts, changes, () => false), "저장 실패 결과 전달");
        Check(alerts["codex:300"] == original && !alerts.ContainsKey("go:weekly"), "저장 실패 시 알림 상태 롤백");
        Check(AlertStateTransaction.Apply(alerts, changes, () => true), "저장 성공 결과 전달");
        Check(alerts["codex:300"].Level == 2 && alerts["go:weekly"].Level == 1, "저장 성공 시 알림 상태 확정");

        var saved = false;
        using (var form = new AlertPolicyForm(settings, quiet, (_, _) => { saved = true; return null; }))
        {
            form.Show();
            Application.DoEvents();
            Check(form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<NumericUpDown>().Count() == 15,
                "다섯 구간의 경고·위험·반복 설정");
            Check(form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<CheckBox>().Count() == 5,
                "구간별 알림 사용 여부");
            var quietPanel = form.Controls.OfType<Panel>().Single(panel => panel.Controls.OfType<CheckBox>()
                .Any(check => check.Text == "방해 금지 사용"));
            Check(quietPanel.Controls.Cast<Control>().All(control => control.Left >= quietPanel.Padding.Left
                && control.Right <= quietPanel.Width - quietPanel.Padding.Right
                && control.Top >= quietPanel.Padding.Top
                && control.Bottom <= quietPanel.Height - quietPanel.Padding.Bottom),
                "방해 금지 컨트롤이 패널 패딩 안쪽에 표시");
            Directory.CreateDirectory("obj/layout-verification");
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(System.Drawing.Point.Empty, form.Size));
                bitmap.Save("obj/layout-verification/alert-policy.png");
            }
            form.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>()
                .Single(button => button.Text == "저장").PerformClick();
            Check(saved, "알림 정책 저장 콜백");
        }
        Console.WriteLine($"알림 정책 검증 {checks}개 통과");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks++;
    }

    private sealed class AlertPolicySettingsDocument
    {
        public AlertPolicySettings AlertPolicies { get; set; } = new();
    }
}
