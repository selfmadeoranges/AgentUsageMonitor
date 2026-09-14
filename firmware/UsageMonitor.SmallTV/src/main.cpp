#include <Arduino.h>
#include <DNSServer.h>
#include <ESP8266HTTPClient.h>
#include <ESP8266WebServer.h>
#include <ESP8266WiFi.h>
#include <ESP8266mDNS.h>
#include <LittleFS.h>
#include <TFT_eSPI.h>
#include <Updater.h>
#include <WiFiClientSecureBearSSL.h>
#include <time.h>

#include "RecoveryValidation.h"
#include "TlsRoots.h"
#include "UsageData.h"

#if __has_include("SmallTvConfig.h")
#include "SmallTvConfig.h"
#else
#include "SmallTvConfig.example.h"
#endif

constexpr uint8_t BACKLIGHT_PIN = 5;
constexpr uint32_t RECONNECT_MS = 30000;
constexpr uint32_t HTTP_TIMEOUT_MS = 8000;
constexpr uint16_t BG = 0x1082;
constexpr uint16_t PANEL = 0x2104;
constexpr uint16_t MUTED = 0x8C51;
constexpr uint16_t GREEN = 0x4D6A;
constexpr uint16_t ORANGE = 0xFD24;
constexpr uint16_t RED = 0xF28A;

constexpr char CLAUDE_USAGE_URL[] = "https://api.anthropic.com/api/oauth/usage";
constexpr char CLAUDE_TOKEN_URL[] = "https://console.anthropic.com/v1/oauth/token";
constexpr char CLAUDE_CLIENT_ID[] = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
constexpr char CODEX_USAGE_URL[] = "https://chatgpt.com/backend-api/wham/usage";
constexpr char CODEX_TOKEN_URL[] = "https://auth.openai.com/oauth/token";
constexpr char CODEX_CLIENT_ID[] = "app_EMoamEEZ73f0CkXaXp7hrann";

static_assert(PAGE_INTERVAL_MS >= 1000, "Page interval must be at least one second");
static_assert(sizeof(WIFI_SSID) <= 33, "Wi-Fi SSID is limited to 32 bytes");
static_assert(sizeof(WIFI_PASSWORD) <= 64, "Wi-Fi password is limited to 63 bytes");
static_assert(sizeof(ADMIN_PASSWORD) <= 32, "Admin password is limited to 31 bytes");
static_assert(sizeof(ADMIN_PASSWORD) == 1 || sizeof(ADMIN_PASSWORD) >= 13, "Admin password must be empty or at least 12 bytes");

struct DeviceSettings {
    String ssid;
    String password;
    uint16_t wifiDelaySeconds = 0;
    uint16_t refreshSeconds = 120;
    uint8_t brightness = 190;
    bool nightEnabled = false;
    uint8_t nightStartHour = 22;
    uint8_t nightEndHour = 7;
    uint8_t nightBrightness = 40;
    int8_t utcOffsetHours = 9;
};

struct ProviderCredentials {
    String accessToken;
    String refreshToken;
    String accountId;
    int64_t expiresAt = 0;
};

TFT_eSPI tft;
ESP8266WebServer webServer(80);
DNSServer dnsServer;
BearSSL::X509List trustAnchors(TLS_ROOTS);
DeviceSettings settings;
ProviderCredentials claudeCredentials;
ProviderCredentials codexCredentials;
UsageData usage;

uint32_t wifiConnectAt = 0;
uint32_t lastReconnectAt = 0;
uint32_t lastPageChangeAt = 0;
uint32_t lastDrawAt = 0;
uint32_t restartAt = 0;
uint32_t nextPollAt[MAX_TOOLS] = {};
size_t displayPage = 0;
size_t nextProvider = 0;
bool wifiBeginSent = false;
bool recoveryActive = false;
bool recoveryDrawn = false;
bool fileSystemMounted = false;
bool ntpStarted = false;
bool mdnsStarted = false;
bool otaAuthenticated = false;
bool otaFailed = false;
char recoverySsid[32] = {};
char adminPassword[32] = {};
const char* connectionState = "STARTING";

void copyToolNames() {
    snprintf(usage.tools[0].name, sizeof(usage.tools[0].name), "Claude");
    snprintf(usage.tools[1].name, sizeof(usage.tools[1].name), "Codex");
}

int64_t unixNow() {
    time_t current = time(nullptr);
    return current > 1700000000 ? static_cast<int64_t>(current) : -1;
}

bool timeReady() {
    return unixNow() > 0;
}

void drawText(int x, int y, const char* text, uint16_t color, int font = 2) {
    tft.setTextColor(color, BG);
    tft.drawString(text, x, y, font);
}

void applyBrightness() {
    uint8_t value = settings.brightness;
    int64_t now = unixNow();
    if (settings.nightEnabled && now > 0) {
        time_t local = static_cast<time_t>(now + static_cast<int64_t>(settings.utcOffsetHours) * 3600);
        tm valueLocal = {};
        gmtime_r(&local, &valueLocal);
        bool night = settings.nightStartHour < settings.nightEndHour
            ? valueLocal.tm_hour >= settings.nightStartHour && valueLocal.tm_hour < settings.nightEndHour
            : valueLocal.tm_hour >= settings.nightStartHour || valueLocal.tm_hour < settings.nightEndHour;
        if (night) value = settings.nightBrightness;
    }
    analogWrite(BACKLIGHT_PIN, 255 - value);
}

bool ensureFileSystemForWrite() {
    if (fileSystemMounted) return true;
    if (!LittleFS.format() || !LittleFS.begin()) return false;
    fileSystemMounted = true;
    return true;
}

bool writeJsonAtomically(const char* path, JsonDocument& document) {
    if (!ensureFileSystemForWrite()) return false;
    String temporary = String(path) + F(".tmp");
    File file = LittleFS.open(temporary, "w");
    if (!file) return false;
    bool written = serializeJson(document, file) > 0;
    file.close();
    if (!written) {
        LittleFS.remove(temporary);
        return false;
    }
    LittleFS.remove(path);
    return LittleFS.rename(temporary, path);
}

void loadSettings() {
    settings.ssid = WIFI_SSID;
    settings.password = WIFI_PASSWORD;
    fileSystemMounted = LittleFS.begin();
    if (!fileSystemMounted || !LittleFS.exists("/settings.json")) return;
    File file = LittleFS.open("/settings.json", "r");
    if (!file || file.size() > 2048) return;
    StaticJsonDocument<2048> document;
    if (deserializeJson(document, file) || document["version"] != 2) return;
    String ssid = document["wifiSsid"] | "";
    String password = document["wifiPassword"] | "";
    long wifiDelay = document["wifiDelaySeconds"] | 0;
    long refresh = document["refreshSeconds"] | 120;
    long brightness = document["brightness"] | 190;
    long nightStart = document["nightStartHour"] | 22;
    long nightEnd = document["nightEndHour"] | 7;
    long nightBrightness = document["nightBrightness"] | 40;
    long utcOffset = document["utcOffsetHours"] | 9;
    if ((!ssid.isEmpty() && !isValidWifiSsid(ssid.c_str())) || !isValidWifiPassword(password.c_str())
        || !isValidWifiDelay(wifiDelay) || !isValidRefreshSeconds(refresh) || !isValidBrightness(brightness)
        || !isValidHour(nightStart) || !isValidHour(nightEnd) || !isValidBrightness(nightBrightness)
        || !isValidUtcOffset(utcOffset)) return;
    settings.ssid = ssid;
    settings.password = password;
    settings.wifiDelaySeconds = static_cast<uint16_t>(wifiDelay);
    settings.refreshSeconds = static_cast<uint16_t>(refresh);
    settings.brightness = static_cast<uint8_t>(brightness);
    settings.nightEnabled = document["nightEnabled"] | false;
    settings.nightStartHour = static_cast<uint8_t>(nightStart);
    settings.nightEndHour = static_cast<uint8_t>(nightEnd);
    settings.nightBrightness = static_cast<uint8_t>(nightBrightness);
    settings.utcOffsetHours = static_cast<int8_t>(utcOffset);
}

bool saveSettings() {
    StaticJsonDocument<2048> document;
    document["version"] = 2;
    document["wifiSsid"] = settings.ssid;
    document["wifiPassword"] = settings.password;
    document["wifiDelaySeconds"] = settings.wifiDelaySeconds;
    document["refreshSeconds"] = settings.refreshSeconds;
    document["brightness"] = settings.brightness;
    document["nightEnabled"] = settings.nightEnabled;
    document["nightStartHour"] = settings.nightStartHour;
    document["nightEndHour"] = settings.nightEndHour;
    document["nightBrightness"] = settings.nightBrightness;
    document["utcOffsetHours"] = settings.utcOffsetHours;
    return writeJsonAtomically("/settings.json", document);
}

bool loadCredentials(const char* path, ProviderCredentials& credentials) {
    if (!fileSystemMounted || !LittleFS.exists(path)) return false;
    File file = LittleFS.open(path, "r");
    if (!file || file.size() > 12288) return false;
    DynamicJsonDocument document(12288);
    if (deserializeJson(document, file) || document["version"] != 1) return false;
    const char* access = document["accessToken"] | "";
    const char* refresh = document["refreshToken"] | "";
    const char* account = document["accountId"] | "";
    if (!isValidToken(access) || !isValidToken(refresh) || strlen(account) > 128) return false;
    credentials.accessToken = access;
    credentials.refreshToken = refresh;
    credentials.accountId = account;
    credentials.expiresAt = document["expiresAt"] | 0;
    return !credentials.accessToken.isEmpty() || !credentials.refreshToken.isEmpty();
}

bool saveCredentials(const char* path, const ProviderCredentials& credentials) {
    DynamicJsonDocument document(12288);
    document["version"] = 1;
    document["accessToken"] = credentials.accessToken;
    document["refreshToken"] = credentials.refreshToken;
    document["accountId"] = credentials.accountId;
    document["expiresAt"] = credentials.expiresAt;
    return writeJsonAtomically(path, document);
}

String htmlEscape(const String& input) {
    String output;
    output.reserve(input.length() + 16);
    for (size_t i = 0; i < input.length(); i++) {
        switch (input[i]) {
            case '&': output += F("&amp;"); break;
            case '<': output += F("&lt;"); break;
            case '>': output += F("&gt;"); break;
            case '\"': output += F("&quot;"); break;
            case '\'': output += F("&#39;"); break;
            default: output += input[i]; break;
        }
    }
    return output;
}

String pageStart(const __FlashStringHelper* title) {
    String html;
    html.reserve(4200);
    html = F("<!doctype html><html lang=ko><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'>"
        "<title>SmallTV 사용량</title><style>body{margin:0;background:#36393e;color:#ddd;font:16px Arial,'Noto Sans KR',sans-serif}"
        "nav{background:#2f3136;padding:18px;text-align:center}nav a{color:#ddd;margin:0 10px;text-decoration:none}"
        "main{max-width:720px;margin:20px auto;padding:0 16px}section{background:#2f3136;padding:18px;margin:16px 0;border-left:5px solid #f33}"
        "label{display:block;margin:12px 0 5px}input,select{box-sizing:border-box;width:100%;max-width:520px;padding:10px;background:#292b2f;color:#fff;border:1px solid #555}"
        "input[type=checkbox]{width:auto}button,.button{display:inline-block;background:#2878e8;color:white;border:0;padding:11px 18px;margin:12px 6px 0 0;text-decoration:none;font-weight:bold}"
        ".danger{background:#c33}.muted{color:#aaa}.ok{color:#70d090}.bad{color:#ff9078}table{border-collapse:collapse;width:100%}td{padding:6px;border-bottom:1px solid #555}</style></head><body>"
        "<nav><a href='/'>상태</a><a href='/network.html'>네트워크</a><a href='/accounts.html'>계정</a><a href='/settings.html'>설정</a></nav><main><h1>");
    html += title;
    html += F("</h1>");
    return html;
}

void sendHtml(String& html) {
    html += F("</main></body></html>");
    webServer.sendHeader(F("Cache-Control"), F("no-store"));
    webServer.send(200, F("text/html; charset=utf-8"), html);
}

bool requireAuthentication() {
    if (webServer.authenticate("admin", adminPassword)) return true;
    webServer.requestAuthentication(BASIC_AUTH, "SmallTV Usage");
    return false;
}

String currentIpText() {
    if (WiFi.status() == WL_CONNECTED) return WiFi.localIP().toString();
    if (recoveryActive) return WiFi.softAPIP().toString();
    return F("not assigned");
}

const __FlashStringHelper* connectionStateForWeb() {
    if (strcmp(connectionState, "STARTING") == 0) return F("시작 중");
    if (strcmp(connectionState, "CONNECTING") == 0) return F("Wi-Fi 연결 중");
    if (strcmp(connectionState, "AP START FAILED") == 0) return F("복구 AP 시작 실패");
    if (strcmp(connectionState, "RECOVERY AP") == 0) return F("복구 AP 사용 중");
    if (strcmp(connectionState, "WI-FI OFFLINE") == 0) return F("Wi-Fi 연결 끊김");
    if (strcmp(connectionState, "WI-FI DELAY") == 0) return F("Wi-Fi 연결 대기");
    if (strcmp(connectionState, "ONLINE") == 0) return F("온라인");
    if (strcmp(connectionState, "TIME SYNC") == 0) return F("시간 동기화 중");
    return F("상태 확인 중");
}

const __FlashStringHelper* toolStateForWeb(const char* state) {
    if (strcmp(state, "waiting") == 0) return F("조회 대기");
    if (strcmp(state, "ok") == 0) return F("정상");
    if (strcmp(state, "auth_required") == 0) return F("계정 설정 필요");
    if (strcmp(state, "auth_error") == 0) return F("인증 오류");
    if (strcmp(state, "rate_limited") == 0) return F("요청 제한");
    if (strcmp(state, "invalid_response") == 0) return F("응답 형식 오류");
    if (strcmp(state, "network_error") == 0) return F("네트워크 오류");
    return F("알 수 없음");
}

String windowLabelForWeb(const char* label) {
    if (strcmp(label, "5 hours") == 0) return F("5시간");
    if (strcmp(label, "Week") == 0) return F("주간");
    return String(label);
}

void handleStatusPage() {
    if (!requireAuthentication()) return;
    String html = pageStart(F("사용량 모니터"));
    html += F("<section><h2>네트워크</h2><table><tr><td>상태</td><td>");
    html += connectionStateForWeb();
    html += F("</td></tr><tr><td>IP</td><td>");
    html += currentIpText();
    html += F("</td></tr><tr><td>Wi-Fi</td><td>");
    html += htmlEscape(settings.ssid);
    html += F("</td></tr><tr><td>호스트 이름</td><td>usage-smalltv.local</td></tr></table></section>");
    for (size_t i = 0; i < MAX_TOOLS; i++) {
        const ToolData& tool = usage.tools[i];
        html += F("<section><h2>");
        html += tool.name;
        html += F("</h2><p>상태: <b>");
        html += toolStateForWeb(tool.state);
        html += F("</b></p><table>");
        for (size_t j = 0; j < tool.windowCount; j++) {
            html += F("<tr><td>"); html += windowLabelForWeb(tool.windows[j].label); html += F("</td><td>");
            html += String(tool.windows[j].percent, 0); html += F("%</td></tr>");
        }
        html += F("</table></section>");
    }
    sendHtml(html);
}

void handleNetworkPage() {
    if (!requireAuthentication()) return;
    String html = pageStart(F("네트워크"));
    html += F("<section><h2>1. Wi-Fi 검색</h2><p class=muted>2.4 GHz 네트워크만 지원합니다.</p>"
        "<form method=get action=/network.html><button name=scan value=1>검색</button></form>");
    if (webServer.arg("scan") == "1") {
        int count = WiFi.scanNetworks(false, true);
        html += F("<label>검색된 네트워크<select onchange=\"document.getElementById('ssid').value=this.value\"><option value=''>선택...</option>");
        for (int i = 0; i < count; i++) {
            String ssid = htmlEscape(WiFi.SSID(i));
            html += F("<option value=\""); html += ssid; html += F("\">"); html += ssid;
            html += F(" ("); html += WiFi.RSSI(i); html += F(" dBm)</option>");
        }
        html += F("</select></label>");
        WiFi.scanDelete();
    }
    html += F("</section><section><h2>2. Wi-Fi 설정</h2><form method=post action=/save-network>"
        "<label>SSID<input id=ssid name=ssid maxlength=32 required value=\"");
    html += htmlEscape(settings.ssid);
    html += F("\"></label><label>비밀번호<input type=password name=password maxlength=63 placeholder='비워 두면 기존 비밀번호 유지'></label>"
        "<label>연결 시작 지연 (초, 0~300)<input type=number name=delay min=0 max=300 value='");
    html += settings.wifiDelaySeconds;
    html += F("'></label><button type=submit>저장 후 재부팅</button></form></section>");
    sendHtml(html);
}

void handleSaveNetwork() {
    if (!requireAuthentication()) return;
    String ssid = webServer.arg("ssid");
    String password = webServer.arg("password");
    long delay = webServer.arg("delay").toInt();
    if (password.isEmpty() && ssid == settings.ssid) password = settings.password;
    if (!isValidWifiSsid(ssid.c_str()) || !isValidWifiPassword(password.c_str()) || !isValidWifiDelay(delay)) {
        webServer.send(400, F("text/plain; charset=utf-8"), F("Wi-Fi 설정이 올바르지 않습니다."));
        return;
    }
    settings.ssid = ssid;
    settings.password = password;
    settings.wifiDelaySeconds = static_cast<uint16_t>(delay);
    if (!saveSettings()) {
        webServer.send(500, F("text/plain; charset=utf-8"), F("설정을 저장하지 못했습니다."));
        return;
    }
    webServer.send(200, F("text/html; charset=utf-8"), F("<meta charset=utf-8><h2>저장했습니다. 재부팅 중...</h2>"));
    restartAt = millis() + 1200;
}

String credentialsState(const ProviderCredentials& credentials) {
    if (credentials.accessToken.isEmpty() && credentials.refreshToken.isEmpty()) return F("설정되지 않음");
    if (!credentials.refreshToken.isEmpty()) return F("설정됨, 자동 갱신 사용");
    return F("액세스 토큰만 설정됨");
}

void handleAccountsPage() {
    if (!requireAuthentication()) return;
    String html = pageStart(F("계정"));
    html += F("<p class=muted>인증 정보는 LittleFS에 저장되며 다시 표시되지 않습니다. 빈 입력란은 기존 값을 유지합니다.</p>"
        "<section><h2>계정 등록 묶음</h2><form method=post action=/save-bundle>"
        "<label>tools/export-smalltv-credentials.ps1에서 출력한 JSON 붙여넣기<textarea name=bundle rows=8 style='box-sizing:border-box;width:100%;background:#292b2f;color:#fff' required></textarea></label>"
        "<button type=submit>등록</button></form></section>"
        "<section><h2>Claude</h2><p>");
    html += credentialsState(claudeCredentials);
    html += F("</p><form method=post action=/save-account><input type=hidden name=provider value=claude>"
        "<label>액세스 토큰<input type=password name=access maxlength=4096 autocomplete=off></label>"
        "<label>리프레시 토큰<input type=password name=refresh maxlength=4096 autocomplete=off></label>"
        "<label>만료 시각 (Unix 초, 알 수 없으면 0)<input type=number name=expires min=0 value=0></label>"
        "<label><input type=checkbox name=clear value=1> Claude 인증 정보 삭제</label><button type=submit>저장</button></form></section>"
        "<section><h2>Codex</h2><p>");
    html += credentialsState(codexCredentials);
    html += F("</p><form method=post action=/save-account><input type=hidden name=provider value=codex>"
        "<label>액세스 토큰<input type=password name=access maxlength=4096 autocomplete=off></label>"
        "<label>리프레시 토큰<input type=password name=refresh maxlength=4096 autocomplete=off></label>"
        "<label>ChatGPT 계정 ID<input name=account maxlength=128 autocomplete=off></label>"
        "<label>만료 시각 (Unix 초, 알 수 없으면 0)<input type=number name=expires min=0 value=0></label>"
        "<label><input type=checkbox name=clear value=1> Codex 인증 정보 삭제</label><button type=submit>저장</button></form></section>");
    sendHtml(html);
}

bool readCredentialObject(JsonObjectConst object, ProviderCredentials& credentials, bool requireAccount) {
    if (object.isNull()) return false;
    const char* access = object["accessToken"] | "";
    const char* refresh = object["refreshToken"] | "";
    const char* account = object["accountId"] | "";
    if (!isValidToken(access) || !isValidToken(refresh) || strlen(account) > 128
        || (strlen(access) == 0 && strlen(refresh) == 0) || (requireAccount && strlen(account) == 0)) return false;
    credentials.accessToken = access;
    credentials.refreshToken = refresh;
    credentials.accountId = account;
    credentials.expiresAt = object["expiresAt"] | 0;
    return credentials.expiresAt >= 0;
}

void handleSaveBundle() {
    if (!requireAuthentication()) return;
    String bundle = webServer.arg("bundle");
    if (bundle.length() == 0 || bundle.length() > 12000) {
        webServer.send(400, F("text/plain; charset=utf-8"), F("계정 등록 묶음의 크기가 올바르지 않습니다."));
        return;
    }
    DynamicJsonDocument document(12288);
    if (deserializeJson(document, bundle)) {
        webServer.send(400, F("text/plain; charset=utf-8"), F("계정 등록 JSON이 올바르지 않습니다."));
        return;
    }
    ProviderCredentials nextClaude;
    ProviderCredentials nextCodex;
    if (!readCredentialObject(document["claude"].as<JsonObjectConst>(), nextClaude, false)
        || !readCredentialObject(document["codex"].as<JsonObjectConst>(), nextCodex, true)
        || !saveCredentials("/claude.json", nextClaude) || !saveCredentials("/codex.json", nextCodex)) {
        webServer.send(400, F("text/plain; charset=utf-8"), F("계정 정보가 부족하거나 저장하지 못했습니다."));
        return;
    }
    claudeCredentials = nextClaude;
    codexCredentials = nextCodex;
    for (size_t i = 0; i < MAX_TOOLS; i++) {
        usage.tools[i].windowCount = 0;
        snprintf(usage.tools[i].state, sizeof(usage.tools[i].state), "waiting");
        nextPollAt[i] = 0;
    }
    webServer.sendHeader(F("Location"), F("/accounts.html"), true);
    webServer.send(303, F("text/plain; charset=utf-8"), F("등록했습니다."));
}

void handleSaveAccount() {
    if (!requireAuthentication()) return;
    bool claude = webServer.arg("provider") == "claude";
    bool codex = webServer.arg("provider") == "codex";
    if (!claude && !codex) {
        webServer.send(400, F("text/plain; charset=utf-8"), F("공급자 정보가 올바르지 않습니다."));
        return;
    }
    ProviderCredentials& credentials = claude ? claudeCredentials : codexCredentials;
    const char* path = claude ? "/claude.json" : "/codex.json";
    if (webServer.arg("clear") == "1") {
        credentials = ProviderCredentials();
        if (fileSystemMounted) LittleFS.remove(path);
    } else {
        String access = webServer.arg("access");
        String refresh = webServer.arg("refresh");
        String account = webServer.arg("account");
        if (!access.isEmpty()) credentials.accessToken = access;
        if (!refresh.isEmpty()) credentials.refreshToken = refresh;
        if (codex && !account.isEmpty()) credentials.accountId = account;
        String expiry = webServer.arg("expires");
        if (!expiry.isEmpty() && expiry != "0") credentials.expiresAt = strtoll(expiry.c_str(), nullptr, 10);
        if (!isValidToken(credentials.accessToken.c_str()) || !isValidToken(credentials.refreshToken.c_str())
            || credentials.accountId.length() > 128 || !saveCredentials(path, credentials)) {
            webServer.send(400, F("text/plain; charset=utf-8"), F("인증 정보가 올바르지 않거나 저장하지 못했습니다."));
            return;
        }
    }
    ToolData& tool = usage.tools[claude ? 0 : 1];
    tool.windowCount = 0;
    snprintf(tool.state, sizeof(tool.state), credentials.accessToken.isEmpty() && credentials.refreshToken.isEmpty() ? "auth_required" : "waiting");
    nextPollAt[claude ? 0 : 1] = 0;
    webServer.sendHeader(F("Location"), F("/accounts.html"), true);
    webServer.send(303, F("text/plain; charset=utf-8"), F("저장했습니다."));
}

void handleSettingsPage() {
    if (!requireAuthentication()) return;
    String html = pageStart(F("설정"));
    html += F("<section><h2>3. 밝기</h2><form method=post action=/save-display>"
        "<label>주간 밝기 (0~255)<input type=range name=brightness min=0 max=255 value='");
    html += settings.brightness;
    html += F("'></label><h3>4. 야간 모드</h3><label><input type=checkbox name=night value=1 ");
    if (settings.nightEnabled) html += F("checked");
    html += F("> 사용</label><label>시작 시각<input type=number name=start min=0 max=23 value='"); html += settings.nightStartHour;
    html += F("'></label><label>종료 시각<input type=number name=end min=0 max=23 value='"); html += settings.nightEndHour;
    html += F("'></label><label>야간 밝기<input type=range name=night_brightness min=0 max=255 value='"); html += settings.nightBrightness;
    html += F("'></label><label>UTC 오프셋 (-12~14)<input type=number name=utc min=-12 max=14 value='"); html += settings.utcOffsetHours;
    html += F("'></label><label>사용량 갱신 주기 (초, 60~3600)<input type=number name=refresh min=60 max=3600 value='"); html += settings.refreshSeconds;
    html += F("'></label><button type=submit>저장</button></form></section>"
        "<section><h2>5. 공장 초기화</h2><p>Wi-Fi, 화면 설정과 계정 인증 정보를 모두 삭제합니다.</p>"
        "<form method=post action=/factory-reset onsubmit=\"return confirm('모든 설정을 삭제하시겠습니까?')\"><button class=danger>초기화</button></form></section>"
        "<section><h2>6. 재부팅</h2><form method=post action=/reboot><button class=danger>재부팅</button></form></section>"
        "<section><h2>7. 펌웨어 업데이트</h2><p>호환되는 펌웨어 BIN 파일을 업로드합니다.</p><a class='button danger' href='/update'>업데이트</a></section>");
    sendHtml(html);
}

void handleUpdatePage() {
    if (!requireAuthentication()) return;
    String html = pageStart(F("펌웨어 업데이트"));
    html += F("<section><h2>펌웨어 BIN 업로드</h2><p class=muted>SmallTV-Ultra용으로 빌드한 호환 BIN 파일만 선택하세요.</p>"
        "<form method=post action=/update enctype='multipart/form-data'>"
        "<label>펌웨어 파일<input type=file name=update accept='.bin,application/octet-stream' required></label>"
        "<button class=danger type=submit>업로드 및 재부팅</button></form></section>");
    sendHtml(html);
}

void handleUpdateUpload() {
    HTTPUpload& upload = webServer.upload();
    if (upload.status == UPLOAD_FILE_START) {
        otaAuthenticated = webServer.authenticate("admin", adminPassword);
        otaFailed = false;
        if (!otaAuthenticated) return;
        uint32_t maxSketchSpace = (ESP.getFreeSketchSpace() - 0x1000) & 0xFFFFF000;
        otaFailed = !Update.begin(maxSketchSpace, U_FLASH);
    } else if (otaAuthenticated && upload.status == UPLOAD_FILE_WRITE && !otaFailed) {
        otaFailed = Update.write(upload.buf, upload.currentSize) != upload.currentSize;
    } else if (otaAuthenticated && upload.status == UPLOAD_FILE_END) {
        if (!otaFailed) otaFailed = !Update.end(true);
        else Update.end();
    } else if (otaAuthenticated && upload.status == UPLOAD_FILE_ABORTED) {
        Update.end();
        otaFailed = true;
    }
    yield();
}

void handleUpdateComplete() {
    if (!otaAuthenticated) {
        webServer.requestAuthentication(BASIC_AUTH, "SmallTV Usage");
        return;
    }
    otaAuthenticated = false;
    if (otaFailed || Update.hasError()) {
        webServer.send(500, F("text/html; charset=utf-8"),
            F("<meta charset=utf-8><h2>펌웨어 업데이트에 실패했습니다.</h2><p><a href='/update'>다시 시도</a></p>"));
        return;
    }
    webServer.send(200, F("text/html; charset=utf-8"),
        F("<meta charset=utf-8><meta http-equiv=refresh content='15;URL=/'><h2>업데이트했습니다. 재부팅 중...</h2>"));
    restartAt = millis() + 1200;
}

void handleSaveDisplay() {
    if (!requireAuthentication()) return;
    long brightness = webServer.arg("brightness").toInt();
    long start = webServer.arg("start").toInt();
    long end = webServer.arg("end").toInt();
    long nightBrightness = webServer.arg("night_brightness").toInt();
    long utc = webServer.arg("utc").toInt();
    long refresh = webServer.arg("refresh").toInt();
    if (!isValidBrightness(brightness) || !isValidHour(start) || !isValidHour(end)
        || !isValidBrightness(nightBrightness) || !isValidUtcOffset(utc) || !isValidRefreshSeconds(refresh)) {
        webServer.send(400, F("text/plain; charset=utf-8"), F("화면 설정이 올바르지 않습니다."));
        return;
    }
    settings.brightness = static_cast<uint8_t>(brightness);
    settings.nightEnabled = webServer.arg("night") == "1";
    settings.nightStartHour = static_cast<uint8_t>(start);
    settings.nightEndHour = static_cast<uint8_t>(end);
    settings.nightBrightness = static_cast<uint8_t>(nightBrightness);
    settings.utcOffsetHours = static_cast<int8_t>(utc);
    settings.refreshSeconds = static_cast<uint16_t>(refresh);
    if (!saveSettings()) {
        webServer.send(500, F("text/plain; charset=utf-8"), F("설정을 저장하지 못했습니다."));
        return;
    }
    applyBrightness();
    webServer.sendHeader(F("Location"), F("/settings.html"), true);
    webServer.send(303, F("text/plain; charset=utf-8"), F("저장했습니다."));
}

void handleFactoryReset() {
    if (!requireAuthentication()) return;
    settings = DeviceSettings();
    if (fileSystemMounted) {
        LittleFS.remove("/claude.json");
        LittleFS.remove("/codex.json");
    }
    bool saved = saveSettings();
    if (!saved) {
        webServer.send(500, F("text/plain; charset=utf-8"), F("설정을 초기화하지 못했습니다."));
        return;
    }
    webServer.send(200, F("text/html; charset=utf-8"), F("<meta charset=utf-8><h2>초기화했습니다. 재부팅 중...</h2>"));
    restartAt = millis() + 1200;
}

void handleReboot() {
    if (!requireAuthentication()) return;
    webServer.send(200, F("text/html; charset=utf-8"), F("<meta charset=utf-8><h2>재부팅 중...</h2>"));
    restartAt = millis() + 800;
}

void handleStatusJson() {
    if (!requireAuthentication()) return;
    StaticJsonDocument<2048> document;
    document["model"] = "SmallTV-Ultra";
    document["firmware"] = "UsageMonitor-0.2.5";
    document["ip"] = currentIpText();
    document["wifiConnected"] = WiFi.status() == WL_CONNECTED;
    document["recoveryAp"] = recoveryActive;
    document["timeReady"] = timeReady();
    document["freeHeap"] = ESP.getFreeHeap();
    JsonArray tools = document.createNestedArray("tools");
    for (size_t i = 0; i < MAX_TOOLS; i++) {
        JsonObject item = tools.createNestedObject();
        item["name"] = usage.tools[i].name;
        item["state"] = usage.tools[i].state;
        item["httpStatus"] = usage.tools[i].httpStatus;
        item["lastSuccessAt"] = usage.tools[i].lastSuccessAt;
        item["retryAt"] = usage.tools[i].retryAt;
    }
    String body;
    serializeJson(document, body);
    webServer.sendHeader(F("Cache-Control"), F("no-store"));
    webServer.send(200, F("application/json"), body);
}

void configureWebServer() {
    snprintf(recoverySsid, sizeof(recoverySsid), "UsageMonitor-%06X", ESP.getChipId());
    if (strlen(ADMIN_PASSWORD) >= 12) snprintf(adminPassword, sizeof(adminPassword), "%s", ADMIN_PASSWORD);
    else snprintf(adminPassword, sizeof(adminPassword), "UM%06X%04X", ESP.getChipId(), ESP.getFlashChipId() & 0xFFFF);
    webServer.on("/", HTTP_GET, handleStatusPage);
    webServer.on("/network.html", HTTP_GET, handleNetworkPage);
    webServer.on("/save-network", HTTP_POST, handleSaveNetwork);
    webServer.on("/accounts.html", HTTP_GET, handleAccountsPage);
    webServer.on("/save-bundle", HTTP_POST, handleSaveBundle);
    webServer.on("/save-account", HTTP_POST, handleSaveAccount);
    webServer.on("/settings.html", HTTP_GET, handleSettingsPage);
    webServer.on("/update", HTTP_GET, handleUpdatePage);
    webServer.on("/update", HTTP_POST, handleUpdateComplete, handleUpdateUpload);
    webServer.on("/save-display", HTTP_POST, handleSaveDisplay);
    webServer.on("/factory-reset", HTTP_POST, handleFactoryReset);
    webServer.on("/reboot", HTTP_POST, handleReboot);
    webServer.on("/status.json", HTTP_GET, handleStatusJson);
    webServer.onNotFound([]() {
        if (recoveryActive && WiFi.status() != WL_CONNECTED) {
            webServer.sendHeader(F("Location"), F("http://192.168.4.1/"), true);
            webServer.send(302, F("text/plain; charset=utf-8"), F("설정 페이지로 이동합니다."));
        } else {
            webServer.send(404, F("text/plain; charset=utf-8"), F("페이지를 찾을 수 없습니다."));
        }
    });
    webServer.begin();
}

bool startRecoveryAp() {
    if (recoveryActive) return true;
    WiFi.mode(WIFI_AP_STA);
    IPAddress address(192, 168, 4, 1);
    WiFi.softAPConfig(address, address, IPAddress(255, 255, 255, 0));
    if (!WiFi.softAP(recoverySsid, adminPassword)) {
        connectionState = "AP START FAILED";
        return false;
    }
    dnsServer.start(53, "*", WiFi.softAPIP());
    recoveryActive = true;
    recoveryDrawn = false;
    connectionState = "RECOVERY AP";
    return true;
}

void stopRecoveryAp() {
    if (!recoveryActive) return;
    dnsServer.stop();
    WiFi.softAPdisconnect(true);
    WiFi.mode(WIFI_STA);
    WiFi.setAutoReconnect(true);
    recoveryActive = false;
    recoveryDrawn = false;
}

String urlEncode(const String& value) {
    const char* hex = "0123456789ABCDEF";
    String result;
    result.reserve(value.length() + 32);
    for (size_t i = 0; i < value.length(); i++) {
        unsigned char c = static_cast<unsigned char>(value[i]);
        if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~') {
            result += static_cast<char>(c);
        } else {
            result += '%'; result += hex[c >> 4]; result += hex[c & 15];
        }
    }
    return result;
}

bool beginSecureRequest(BearSSL::WiFiClientSecure& client, HTTPClient& http, const char* url) {
    client.setTrustAnchors(&trustAnchors);
    client.setBufferSizes(4096, 512);
    client.setTimeout(HTTP_TIMEOUT_MS);
    http.setTimeout(HTTP_TIMEOUT_MS);
    http.setReuse(false);
    if (!http.begin(client, url)) return false;
    http.addHeader(F("Accept"), F("application/json"));
    return true;
}

bool readJsonResponse(HTTPClient& http, DynamicJsonDocument& document) {
    int size = http.getSize();
    if (size > static_cast<int>(MAX_JSON_BYTES)) return false;
    String body = http.getString();
    if (body.isEmpty() || body.length() > MAX_JSON_BYTES) return false;
    document.clear();
    return !deserializeJson(document, body, DeserializationOption::NestingLimit(8));
}

bool refreshClaudeToken() {
    if (claudeCredentials.refreshToken.isEmpty()) return false;
    BearSSL::WiFiClientSecure client;
    HTTPClient http;
    if (!beginSecureRequest(client, http, CLAUDE_TOKEN_URL)) return false;
    DynamicJsonDocument request(claudeCredentials.refreshToken.length() + 512);
    request["grant_type"] = "refresh_token";
    request["client_id"] = CLAUDE_CLIENT_ID;
    request["refresh_token"] = claudeCredentials.refreshToken;
    String payload;
    serializeJson(request, payload);
    http.addHeader(F("Content-Type"), F("application/json"));
    int status = http.POST(payload);
    DynamicJsonDocument response(12288);
    bool parsed = status == HTTP_CODE_OK && readJsonResponse(http, response);
    if (parsed) {
        const char* access = response["access_token"] | "";
        const char* refresh = response["refresh_token"] | "";
        if (strlen(access) == 0 || !isValidToken(access) || !isValidToken(refresh)) parsed = false;
        else {
            claudeCredentials.accessToken = access;
            if (strlen(refresh) != 0) claudeCredentials.refreshToken = refresh;
            claudeCredentials.expiresAt = unixNow() + (response["expires_in"] | 3600);
            parsed = saveCredentials("/claude.json", claudeCredentials);
        }
    }
    Serial.printf("Claude token refresh HTTP=%d ok=%d heap=%u\n", status, parsed, ESP.getFreeHeap());
    http.end();
    return parsed;
}

bool refreshCodexToken() {
    if (codexCredentials.refreshToken.isEmpty()) return false;
    BearSSL::WiFiClientSecure client;
    HTTPClient http;
    if (!beginSecureRequest(client, http, CODEX_TOKEN_URL)) return false;
    http.addHeader(F("Content-Type"), F("application/x-www-form-urlencoded"));
    String payload = F("grant_type=refresh_token&client_id=");
    payload += urlEncode(CODEX_CLIENT_ID);
    payload += F("&refresh_token=");
    payload += urlEncode(codexCredentials.refreshToken);
    payload += F("&scope=openid%20profile%20email");
    int status = http.POST(payload);
    DynamicJsonDocument response(12288);
    bool parsed = status == HTTP_CODE_OK && readJsonResponse(http, response);
    if (parsed) {
        const char* access = response["access_token"] | "";
        const char* refresh = response["refresh_token"] | "";
        if (strlen(access) == 0 || !isValidToken(access) || !isValidToken(refresh)) parsed = false;
        else {
            codexCredentials.accessToken = access;
            if (strlen(refresh) != 0) codexCredentials.refreshToken = refresh;
            codexCredentials.expiresAt = unixNow() + (response["expires_in"] | 3600);
            parsed = saveCredentials("/codex.json", codexCredentials);
        }
    }
    Serial.printf("Codex token refresh HTTP=%d ok=%d heap=%u\n", status, parsed, ESP.getFreeHeap());
    http.end();
    return parsed;
}

bool ensureAccessToken(size_t provider) {
    ProviderCredentials& credentials = provider == 0 ? claudeCredentials : codexCredentials;
    int64_t now = unixNow();
    bool expired = credentials.accessToken.isEmpty() || (credentials.expiresAt > 0 && credentials.expiresAt <= now + 120);
    if (!expired) return true;
    return provider == 0 ? refreshClaudeToken() : refreshCodexToken();
}

int queryUsage(size_t provider, bool allowRefresh) {
    ToolData& tool = usage.tools[provider];
    ProviderCredentials& credentials = provider == 0 ? claudeCredentials : codexCredentials;
    if (!ensureAccessToken(provider) || (provider == 1 && credentials.accountId.isEmpty())) {
        setToolError(tool, "auth_required", 0, unixNow());
        return 0;
    }
    BearSSL::WiFiClientSecure client;
    HTTPClient http;
    const char* url = provider == 0 ? CLAUDE_USAGE_URL : CODEX_USAGE_URL;
    if (!beginSecureRequest(client, http, url)) {
        setToolError(tool, "network_error", 0, unixNow());
        return 0;
    }
    http.addHeader(F("Authorization"), String(F("Bearer ")) + credentials.accessToken);
    if (provider == 0) {
        http.setUserAgent(F("claude-code/2.1.69"));
        http.addHeader(F("anthropic-beta"), F("oauth-2025-04-20"));
    } else {
        http.addHeader(F("ChatGPT-Account-Id"), credentials.accountId);
    }
    int status = http.GET();
    DynamicJsonDocument response(8192);
    int64_t now = unixNow();
    bool parsed = status == HTTP_CODE_OK && readJsonResponse(http, response)
        && (provider == 0 ? parseClaudeUsage(response, tool, now) : parseCodexUsage(response, tool, now));
    http.end();
    if (parsed) {
        tool.httpStatus = status;
        tool.retryAt = 0;
        tool.backoffLevel = 0;
    } else if ((status == HTTP_CODE_UNAUTHORIZED || status == HTTP_CODE_FORBIDDEN) && allowRefresh
        && !credentials.refreshToken.isEmpty() && (provider == 0 ? refreshClaudeToken() : refreshCodexToken())) {
        return queryUsage(provider, false);
    } else if (status == HTTP_CODE_UNAUTHORIZED || status == HTTP_CODE_FORBIDDEN) {
        setToolError(tool, "auth_error", status, now);
    } else if (status == HTTP_CODE_TOO_MANY_REQUESTS) {
        setToolError(tool, "rate_limited", status, now, nextBackoffSeconds(tool));
    } else if (status == HTTP_CODE_OK) {
        setToolError(tool, "invalid_response", status, now);
    } else {
        setToolError(tool, "network_error", status, now);
    }
    Serial.printf("%s usage HTTP=%d valid=%d heap=%u\n", tool.name, status, parsed, ESP.getFreeHeap());
    return status;
}

void scheduleProvider(size_t provider) {
    ToolData& tool = usage.tools[provider];
    uint32_t waitSeconds = settings.refreshSeconds;
    int64_t now = unixNow();
    if (tool.retryAt > now) waitSeconds = static_cast<uint32_t>(tool.retryAt - now);
    nextPollAt[provider] = millis() + waitSeconds * 1000UL;
}

void formatCountdown(char* output, size_t size, int64_t seconds) {
    if (seconds < 0) snprintf(output, size, "reset --");
    else if (seconds == 0) snprintf(output, size, "reset due");
    else if (seconds >= 86400) snprintf(output, size, "reset %ldd %ldh", static_cast<long>(seconds / 86400), static_cast<long>((seconds / 3600) % 24));
    else snprintf(output, size, "reset %ldh %ldm", static_cast<long>(seconds / 3600), static_cast<long>((seconds / 60) % 60));
}

void drawRecoveryPage() {
    tft.fillScreen(BG);
    drawText(12, 12, "WI-FI RECOVERY", TFT_WHITE);
    drawText(12, 47, "Connect to", MUTED);
    drawText(12, 67, recoverySsid, ORANGE);
    drawText(12, 102, "Password", MUTED);
    drawText(12, 122, adminPassword, ORANGE);
    drawText(12, 158, "Open 192.168.4.1", TFT_WHITE);
    drawText(12, 188, "User: admin", MUTED);
}

void drawUsagePage(bool clear) {
    if (recoveryActive && WiFi.status() != WL_CONNECTED) {
        if (clear || !recoveryDrawn) drawRecoveryPage();
        recoveryDrawn = true;
        return;
    }
    recoveryDrawn = false;
    if (clear) tft.fillScreen(BG);
    ToolData& tool = usage.tools[displayPage];
    int64_t now = unixNow();
    bool stale = tool.lastSuccessAt < 0 || now < 0 || now - tool.lastSuccessAt > settings.refreshSeconds * 2 + 30;
    tft.fillRect(0, 0, 240, 38, BG);
    tft.setTextSize(2);
    drawText(12, 9, tool.name, TFT_WHITE, 1);
    tft.setTextSize(1);
    char counter[8];
    snprintf(counter, sizeof(counter), "%u/2", static_cast<unsigned>(displayPage + 1));
    drawText(207, 10, counter, MUTED);
    for (size_t i = 0; i < MAX_WINDOWS; i++) {
        int y = 40 + static_cast<int>(i) * 41;
        tft.fillRect(10, y, 220, 41, BG);
        if (i >= tool.windowCount) continue;
        WindowData& window = tool.windows[i];
        uint16_t color = stale ? MUTED : window.percent >= 95 ? RED : window.percent >= 70 ? ORANGE : GREEN;
        drawText(12, y, window.label, TFT_WHITE);
        char percent[12];
        snprintf(percent, sizeof(percent), "%.0f%%", static_cast<double>(window.percent));
        tft.setTextColor(color, BG);
        tft.drawRightString(percent, 228, y, 2);
        tft.fillRect(12, y + 18, 216, 5, PANEL);
        tft.fillRect(12, y + 18, static_cast<int>(216 * window.percent / 100), 5, color);
        char resetText[32];
        int64_t resetSeconds = window.resetAt < 0 || now < 0 ? -1 : window.resetAt > now ? window.resetAt - now : 0;
        formatCountdown(resetText, sizeof(resetText), resetSeconds);
        drawText(12, y + 27, resetText, MUTED, 1);
    }
    if (tool.windowCount == 0) {
        const char* message = "Usage unavailable";
        if (!timeReady()) message = "Synchronizing time";
        else if (strcmp(tool.state, "auth_required") == 0) message = "Configure account";
        else if (strcmp(tool.state, "waiting") == 0) message = "Loading usage";
        else if (strcmp(tool.state, "rate_limited") == 0) message = "Retrying later";
        drawText(12, 62, message, MUTED);
    }
    tft.fillRect(0, 208, 240, 32, BG);
    const char* state = strcmp(tool.state, "ok") == 0 && stale ? "STALE" : tool.state;
    if (strcmp(state, "rate_limited") == 0) state = "RATE LIMITED";
    else if (strcmp(state, "auth_required") == 0) state = "AUTH REQUIRED";
    else if (strcmp(state, "network_error") == 0) state = "NETWORK ERROR";
    else if (strcmp(state, "invalid_response") == 0) state = "BAD RESPONSE";
    drawText(12, 209, state, strcmp(state, "ok") == 0 ? MUTED : ORANGE, 1);
    String ip = currentIpText();
    String footer = F("IP ");
    footer += ip;
    drawText(12, 225, footer.c_str(), MUTED, 1);
}

void beginWifiIfDue(uint32_t now) {
    if (wifiBeginSent || settings.ssid.isEmpty() || static_cast<int32_t>(now - wifiConnectAt) < 0) return;
    WiFi.begin(settings.ssid.c_str(), settings.password.c_str());
    wifiBeginSent = true;
    connectionState = "CONNECTING";
}

void setup() {
    Serial.begin(115200);
    pinMode(BACKLIGHT_PIN, OUTPUT);
    analogWriteRange(255);
    digitalWrite(BACKLIGHT_PIN, HIGH);
    tft.init();
    tft.setRotation(LCD_ROTATION);
    tft.invertDisplay(LCD_INVERT);
    tft.setTextWrap(false);
    loadSettings();
    loadCredentials("/claude.json", claudeCredentials);
    loadCredentials("/codex.json", codexCredentials);
    copyToolNames();
    if (claudeCredentials.accessToken.isEmpty() && claudeCredentials.refreshToken.isEmpty()) snprintf(usage.tools[0].state, sizeof(usage.tools[0].state), "auth_required");
    if (codexCredentials.accessToken.isEmpty() && codexCredentials.refreshToken.isEmpty()) snprintf(usage.tools[1].state, sizeof(usage.tools[1].state), "auth_required");
    applyBrightness();
    WiFi.persistent(false);
    WiFi.mode(WIFI_STA);
    WiFi.setAutoReconnect(true);
    WiFi.hostname("usage-smalltv");
    wifiConnectAt = millis() + settings.wifiDelaySeconds * 1000UL;
    configureWebServer();
    startRecoveryAp();
    drawUsagePage(true);
}

void loop() {
    uint32_t now = millis();
    webServer.handleClient();
    if (recoveryActive) dnsServer.processNextRequest();
    if (mdnsStarted) MDNS.update();
    if (restartAt != 0 && static_cast<int32_t>(now - restartAt) >= 0) ESP.restart();
    beginWifiIfDue(now);
    bool connected = WiFi.status() == WL_CONNECTED;
    if (!connected) {
        connectionState = recoveryActive ? "RECOVERY AP" : (wifiBeginSent ? "WI-FI OFFLINE" : "WI-FI DELAY");
        if (wifiBeginSent && now - lastReconnectAt >= RECONNECT_MS) {
            lastReconnectAt = now;
            WiFi.reconnect();
        }
    } else {
        connectionState = timeReady() ? "ONLINE" : "TIME SYNC";
        if (!ntpStarted) {
            configTime(0, 0, "pool.ntp.org", "time.google.com");
            ntpStarted = true;
        }
        if (!mdnsStarted) mdnsStarted = MDNS.begin("usage-smalltv");
        if (timeReady()) {
            connectionState = "ONLINE";
            for (size_t attempt = 0; attempt < MAX_TOOLS; attempt++) {
                size_t provider = (nextProvider + attempt) % MAX_TOOLS;
                if (nextPollAt[provider] == 0 || static_cast<int32_t>(now - nextPollAt[provider]) >= 0) {
                    queryUsage(provider, true);
                    scheduleProvider(provider);
                    nextProvider = (provider + 1) % MAX_TOOLS;
                    break;
                }
            }
        }
    }
    now = millis();
    bool pageChanged = now - lastPageChangeAt >= PAGE_INTERVAL_MS;
    if (pageChanged) {
        lastPageChangeAt = now;
        displayPage = (displayPage + 1) % MAX_TOOLS;
    }
    if (pageChanged || now - lastDrawAt >= 1000) {
        lastDrawAt = now;
        applyBrightness();
        drawUsagePage(pageChanged);
    }
    delay(5);
}
