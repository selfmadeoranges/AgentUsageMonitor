#include <cmath>
#include <iostream>
#include <stdexcept>

#include "RecoveryValidation.h"
#include "UsageData.h"

int checks = 0;

void check(bool condition, const char* name) {
    if (!condition) throw std::runtime_error(name);
    std::cout << "PASS: " << name << '\n';
    checks++;
}

int main() {
    constexpr int64_t now = 1789344000; // 2026-09-14T00:00:00Z
    StaticJsonDocument<4096> claude;
    deserializeJson(claude, R"json({
      "five_hour":{"utilization":73.2,"resets_at":"2026-09-14T02:00:00Z"},
      "seven_day":{"utilization":25,"resets_at":"2026-09-20T00:00:00Z"},
      "limits":[
        {"kind":"weekly_scoped","percent":91,"resets_at":"2026-09-21T00:00:00Z","scope":{"model":{"display_name":"Claude Opus"}}},
        {"kind":"weekly_scoped","percent":51.5,"resets_at":"2026-09-21T00:00:00Z","scope":{"model":{"display_name":"Fable"}}}
      ]
    })json");
    ToolData claudeData;
    check(parseClaudeUsage(claude, claudeData, now) && claudeData.windowCount == 3,
          "Claude direct response is parsed");
    check(std::abs(claudeData.windows[0].percent - 73.2f) < 0.01f
          && claudeData.windows[0].resetAt == now + 7200,
          "Claude percentage and ISO reset time are preserved");
    check(strcmp(claudeData.windows[2].label, "Fable") == 0
          && std::abs(claudeData.windows[2].percent - 51.5f) < 0.01f,
          "Claude only keeps the Fable weekly limit");

    StaticJsonDocument<2048> codex;
    deserializeJson(codex, R"json({
      "rate_limit":{"primary_window":{"used_percent":42,"limit_window_seconds":18000,"reset_at":1789362000},
      "secondary_window":{"used_percent":67.5,"limit_window_seconds":604800,"reset_at":1789948800}}
    })json");
    ToolData codexData;
    check(parseCodexUsage(codex, codexData, now) && codexData.windowCount == 1,
          "Codex direct response keeps one window");
    check(strcmp(codexData.windows[0].label, "Week") == 0
          && std::abs(codexData.windows[0].percent - 67.5f) < 0.01f,
          "Codex only keeps the weekly window");
    check(!parseCodexUsage(claude, codexData, now), "provider response shapes are not interchangeable");

    StaticJsonDocument<256> invalidPercent;
    deserializeJson(invalidPercent, R"json({"five_hour":{"utilization":101,"resets_at":"2026-09-14T02:00:00Z"}})json");
    ToolData invalidData;
    check(!parseClaudeUsage(invalidPercent, invalidData, now), "out of range usage is rejected");
    check(parseIso8601Utc("2026-09-14T00:00:00Z") == now
          && parseIso8601Utc("invalid") == -1,
          "UTC timestamp parsing is deterministic");

    ToolData backoff;
    check(nextBackoffSeconds(backoff) == 120 && nextBackoffSeconds(backoff) == 300
          && nextBackoffSeconds(backoff) == 600 && nextBackoffSeconds(backoff) == 900
          && nextBackoffSeconds(backoff) == 900,
          "rate limit backoff is capped at fifteen minutes");
    check(isValidWifiSsid("Office WiFi") && !isValidWifiSsid("")
          && !isValidWifiSsid("123456789012345678901234567890123"),
          "Wi-Fi SSID limits are enforced");
    check(isValidWifiPassword("") && isValidWifiPassword("12345678")
          && !isValidWifiPassword("short"),
          "open and WPA password lengths are distinguished");
    check(isValidWifiDelay(0) && isValidWifiDelay(300) && !isValidWifiDelay(301),
          "connection delay range is enforced");
    check(isValidRefreshSeconds(60) && isValidRefreshSeconds(3600) && !isValidRefreshSeconds(30),
          "usage refresh range is enforced");
    check(isValidBrightness(0) && isValidBrightness(255) && !isValidBrightness(256),
          "brightness range is enforced");
    check(isValidPageIntervalSeconds(2) && isValidPageIntervalSeconds(60)
          && !isValidPageIntervalSeconds(1) && !isValidPageIntervalSeconds(61),
          "page interval range is enforced");
    check(isValidHour(0) && isValidHour(23) && !isValidHour(24)
          && isValidUtcOffset(9) && !isValidUtcOffset(15),
          "night schedule ranges are enforced");
    check(isValidToken("header.payload.signature") && isValidToken("")
          && !isValidToken("token with space"),
          "credential input rejects control and whitespace characters");

    std::cout << "All " << checks << " checks passed.\n";
    return 0;
}
