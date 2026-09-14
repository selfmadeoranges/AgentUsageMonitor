#pragma once

#include <ArduinoJson.h>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>

constexpr size_t MAX_TOOLS = 2;
constexpr size_t MAX_WINDOWS = 3;
constexpr size_t MAX_JSON_BYTES = 12288;

struct WindowData {
    char label[19] = {};
    bool hasPercent = false;
    float percent = 0;
    int64_t resetAt = -1;
};

struct ToolData {
    char name[7] = {};
    char state[18] = "waiting";
    int httpStatus = 0;
    int64_t lastSuccessAt = -1;
    int64_t retryAt = 0;
    uint8_t backoffLevel = 0;
    size_t windowCount = 0;
    WindowData windows[MAX_WINDOWS];
};

struct UsageData {
    ToolData tools[MAX_TOOLS];
    size_t toolCount = MAX_TOOLS;
};

inline bool validPercent(float value) {
    return std::isfinite(value) && value >= 0 && value <= 100;
}

inline int64_t daysFromCivil(int year, unsigned month, unsigned day) {
    year -= month <= 2;
    const int era = (year >= 0 ? year : year - 399) / 400;
    const unsigned yoe = static_cast<unsigned>(year - era * 400);
    const unsigned doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
    const unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return static_cast<int64_t>(era) * 146097 + static_cast<int64_t>(doe) - 719468;
}

inline int64_t parseIso8601Utc(const char* value) {
    if (value == nullptr || strlen(value) < 19) return -1;
    int year = 0;
    int month = 0;
    int day = 0;
    int hour = 0;
    int minute = 0;
    int second = 0;
    if (sscanf(value, "%4d-%2d-%2dT%2d:%2d:%2d", &year, &month, &day, &hour, &minute, &second) != 6) return -1;
    if (year < 1970 || month < 1 || month > 12 || day < 1 || day > 31 || hour > 23 || minute > 59 || second > 60) return -1;
    return daysFromCivil(year, static_cast<unsigned>(month), static_cast<unsigned>(day)) * 86400
        + hour * 3600 + minute * 60 + second;
}

inline bool addWindow(ToolData& tool, const char* label, float percent, int64_t resetAt) {
    if (tool.windowCount >= MAX_WINDOWS || !validPercent(percent)) return false;
    WindowData& window = tool.windows[tool.windowCount++];
    snprintf(window.label, sizeof(window.label), "%s", label);
    window.hasPercent = true;
    window.percent = percent;
    window.resetAt = resetAt;
    return true;
}

inline bool equalsIgnoreCaseAscii(const char* left, const char* right) {
    if (left == nullptr || right == nullptr) return false;
    while (*left != '\0' && *right != '\0') {
        char leftValue = *left >= 'A' && *left <= 'Z' ? static_cast<char>(*left + ('a' - 'A')) : *left;
        char rightValue = *right >= 'A' && *right <= 'Z' ? static_cast<char>(*right + ('a' - 'A')) : *right;
        if (leftValue != rightValue) return false;
        left++;
        right++;
    }
    return *left == '\0' && *right == '\0';
}

inline bool parseClaudeUsage(JsonDocument& doc, ToolData& output, int64_t now) {
    ToolData next;
    snprintf(next.name, sizeof(next.name), "Claude");
    const char* properties[] = { "five_hour", "seven_day" };
    const char* labels[] = { "5 hours", "Week" };
    for (size_t i = 0; i < 2; i++) {
        JsonObjectConst item = doc[properties[i]].as<JsonObjectConst>();
        if (item.isNull() || !item["utilization"].is<float>()) continue;
        addWindow(next, labels[i], item["utilization"].as<float>(), parseIso8601Utc(item["resets_at"] | ""));
    }
    JsonArrayConst limits = doc["limits"].as<JsonArrayConst>();
    for (JsonObjectConst limit : limits) {
        if (strcmp(limit["kind"] | "", "weekly_scoped") != 0 || !limit["percent"].is<float>()) continue;
        const char* modelName = limit["scope"]["model"]["display_name"] | "";
        if (!equalsIgnoreCaseAscii(modelName, "Fable")) continue;
        addWindow(next, "Fable", limit["percent"].as<float>(), parseIso8601Utc(limit["resets_at"] | ""));
        break;
    }
    if (next.windowCount == 0) return false;
    snprintf(next.state, sizeof(next.state), "ok");
    next.lastSuccessAt = now;
    output = next;
    return true;
}

inline bool parseCodexUsage(JsonDocument& doc, ToolData& output, int64_t now) {
    JsonObjectConst rateLimit = doc["rate_limit"].as<JsonObjectConst>();
    if (rateLimit.isNull()) return false;
    ToolData next;
    snprintf(next.name, sizeof(next.name), "Codex");
    const char* properties[] = { "primary_window", "secondary_window" };
    for (const char* property : properties) {
        JsonObjectConst item = rateLimit[property].as<JsonObjectConst>();
        if (item.isNull() || !item["used_percent"].is<float>()) continue;
        int64_t seconds = item["limit_window_seconds"] | 0;
        if (seconds < 6 * 86400) continue;
        addWindow(next, "Week", item["used_percent"].as<float>(), item["reset_at"] | -1);
        break;
    }
    if (next.windowCount == 0) return false;
    snprintf(next.state, sizeof(next.state), "ok");
    next.lastSuccessAt = now;
    output = next;
    return true;
}

inline void setToolError(ToolData& tool, const char* state, int httpStatus, int64_t now, int64_t retrySeconds = 0) {
    snprintf(tool.state, sizeof(tool.state), "%s", state);
    tool.httpStatus = httpStatus;
    if (retrySeconds > 0) tool.retryAt = now + retrySeconds;
}

inline int64_t nextBackoffSeconds(ToolData& tool) {
    static const int64_t steps[] = { 120, 300, 600, 900 };
    size_t index = tool.backoffLevel < 4 ? tool.backoffLevel : 3;
    if (tool.backoffLevel < 255) tool.backoffLevel++;
    return steps[index];
}
