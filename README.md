# UsageMonitor

Claude Code와 Codex CLI의 요금제 한도 사용률을 Windows 11 주 모니터 맨 위나 작업표시줄 바로 위에 한 줄 바로 항상 띄워 두는 위젯입니다.
WPF(.NET 10)로 만들었고, 테두리 없는 Topmost 창을 AppBar로 등록하고 트레이 아이콘으로 제어합니다. Windows 작업표시줄처럼 바가 차지하는 화면 영역을 예약하므로, 다른 창을 최대화해도 바를 가리지 않습니다. 바 자체는 작업표시줄 버튼과 Alt-Tab 목록에 나타나지 않습니다.

## 프로젝트 구조

| 경로 | 프로젝트 |
|---|---|
| `src/UsageMonitor.Desktop` | Windows WPF 사용량 위젯 |
| `src/UsageMonitor.SmallTV.Preview` | Windows용 SmallTV 240×240 화면 미리보기 |
| `firmware/UsageMonitor.SmallTV` | SmallTV-Ultra ESP8266 PlatformIO 펌웨어 |
| `UsageMonitor.slnx` | Windows 데스크톱 프로젝트용 .NET 솔루션 |

SmallTV 펌웨어는 PlatformIO 프로젝트이므로 MSBuild 솔루션과 별도로 빌드합니다.

## 커밋 전 보안 확인

- `firmware/UsageMonitor.SmallTV/include/SmallTvConfig.h`에는 실제 Wi-Fi와 관리자 암호가 들어갈 수 있으므로 커밋하지 않습니다. 저장소에는 빈 값만 있는 `SmallTvConfig.example.h`만 포함합니다.
- `*.bin`, `*.elf`, `*.map`, `*.hex`는 빌드 시 장비 설정을 포함할 수 있어 Git에서 제외합니다. 배포용 펌웨어는 별도 릴리스 저장소에서 관리하세요.
- `smalltv-credentials*.json`, `auth.json`, `.credentials.json`과 OAuth 내보내기 결과를 저장소에 저장하지 않습니다.
- 소스에 포함된 Claude/Codex OAuth client ID와 TLS 루트 인증서는 공개 클라이언트 식별자와 공개 인증서이며 런타임 인증에 필요합니다.

## 표시 내용

- Claude: 5시간 창, 주간 창, 모델별 주간 창(응답에 있을 때만)
- Codex: 응답의 `primary_window` / `secondary_window` (창 길이에 따라 "5시간", "주간" 등으로 표시)
- 각 창의 사용률(%), 막대(70% 이상 주황, 95% 이상 빨강), 리셋까지 남은 시간
- 한도 창마다 이름·사용률 / 막대 / 리셋 시간을 작은 카드로 묶어 가로로 나열하고, 도구 사이에는 구분선을 둡니다.
- 마지막 갱신 시각은 도구 영역에 마우스를 올리면 툴팁으로 표시합니다. 클릭 통과를 켜면 툴팁도 뜨지 않습니다.

## 데이터 출처

| 도구 | 토큰 위치 | 엔드포인트 |
|---|---|---|
| Claude | Windows 자격 증명 관리자 (`UsageMonitor/ClaudeOAuth`) | `https://api.anthropic.com/api/oauth/usage` |
| Codex | Windows 자격 증명 관리자 (`UsageMonitor/CodexOAuth`), 없으면 `%USERPROFILE%\.codex\auth.json` (`CODEX_HOME` 우선) | `https://chatgpt.com/backend-api/wham/usage` |

### Claude 로그인

첫 실행 때 저장된 토큰이 없으면 브라우저에서 Claude OAuth 로그인이 자동으로 열립니다. 허용하면 토큰이 Windows 자격 증명 관리자에 저장되고, 만료 2분 전에 자동 갱신됩니다. Claude Code 설치나 로그인은 필요 없습니다. 요청 범위는 `user:profile` 하나입니다.

로그인을 취소하면 Claude 카드에 "로그인 필요"가 표시되고, 다음 실행 전까지는 다시 묻지 않습니다. 트레이 메뉴의 **Claude 로그인...** 으로 언제든 다시 시작할 수 있고, **Claude 로그아웃**은 저장된 토큰을 삭제합니다.

Claude Code의 `.credentials.json`은 읽지 않습니다. 같은 토큰을 공유하면 양쪽 호출이 한 버킷에 합산되어 429가 나기 쉽기 때문입니다.

### Codex 로그인

Codex도 같은 방식의 브라우저 로그인(ChatGPT 계정, auth.openai.com)을 지원합니다. 트레이 메뉴의 **Codex 로그인...** 으로 받은 토큰은 자격 증명 관리자에 저장되고 만료 전에 자동 갱신됩니다. 계정 ID는 토큰(JWT)의 `chatgpt_account_id` 클레임에서 읽습니다.

Codex 데스크톱 앱과 CLI는 같은 `auth.json`을 쓰므로, 모니터 전용 토큰이 없을 때는 이 파일을 대신 읽습니다. 첫 실행 로그인은 전용 토큰도 `auth.json`도 없을 때만 뜹니다. Claude와 Codex 로그인이 둘 다 필요하면 Claude → Codex 순서로 하나씩 열립니다.

두 엔드포인트는 각 CLI가 내부적으로 쓰는 비공식 API라서 응답 형식이 바뀔 수 있습니다.
모니터 전용 토큰은 Windows 자격 증명 관리자에서 읽고, 만료 2분 전부터 앱이 갱신합니다. Codex의 `auth.json` 대체 경로에서는 파일을 매 조회마다 읽으며 토큰 갱신은 Codex 앱/CLI에 맡깁니다. 인증 정보는 사용량 화면, 설정 파일, HTTP 사용량 응답, 로그에 기록하지 않습니다.

## 트레이 메뉴

- 지금 갱신
- 위젯 표시 (트레이 아이콘 더블클릭으로도 토글): 끄면 예약한 화면 영역도 돌려줍니다.
- 바 위치: 화면 맨 위 / 작업표시줄 바로 위. 기본은 화면 맨 위입니다.
- Claude 표시 / Codex 표시: 끈 도구는 바에서 사라지고 사용량 조회와 알림도 멈춥니다. 둘 중 하나는 항상 켜져 있어야 합니다.
- 클릭 통과: 켜면 마우스 입력이 바 아래 창으로 통과합니다.
- 불투명도: 30 / 50 / 70 / 90 / 100 % (기본 100%)
- Claude 갱신 주기 / Codex 갱신 주기: 각각 30초 / 1분 / 2분 / 5분 / 10분 (하한 30초). 기본은 Claude 2분, Codex 1분입니다. Claude 엔드포인트는 계정 단위 요청 제한이 빡빡해 더 길게 둡니다.
- 95% 도달 시 알림 (on/off)
- Windows 시작 시 실행 (`HKCU\...\Run` 등록)
- 종료

바는 주 모니터 전체 폭으로 표시되며, 높이는 배율 100% 기준 44픽셀입니다. 같은 쪽에 작업표시줄이나 다른 AppBar가 있으면 셸이 겹치지 않는 자리로 붙여 줍니다. 아래쪽을 고르면 작업표시줄 바로 위에 놓입니다.

## 설정 파일

`%APPDATA%\UsageMonitor\settings.json`

```json
{
  "ClaudeRefreshSeconds": 120,
  "CodexRefreshSeconds": 60,
  "Opacity": 1.0,
  "ClickThrough": false,
  "AlertEnabled": true,
  "AlertThreshold": 95,
  "AutoStart": false,
  "WidgetVisible": true,
  "BarAtBottom": false,
  "ShowClaude": true,
  "ShowCodex": true
}
```

`AlertThreshold`는 메뉴에 없고 이 파일에서만 바꿀 수 있습니다.

## 빌드와 배포

```bash
dotnet build UsageMonitor.slnx -c Release
```

SmallTV 펌웨어는 저장소 루트에서 아래 스크립트로 빌드합니다. 첫 실행에는 저장소 내부의 무시된 도구 환경에 PlatformIO를 자동 설치하고, 이후에는 같은 환경을 재사용합니다. 빌드가 끝나면 `firmware.bin` 경로와 SHA-256을 출력합니다.

```powershell
.\tools\Build-SmallTvFirmware.ps1
```

SmallTV 화면은 장비 없이 Windows에서 미리 볼 수 있습니다. Windows DPI를 보정한 실제 240×240 픽셀과 2배 확대 화면을 선택할 수 있습니다. 미리보기 앱은 샘플 사용량만 사용하며 OAuth 토큰이나 장비 설정을 읽지 않습니다.

화면 좌표, RGB565 색상, 표시 문구와 페이지 전환 시간 기본값은 `shared/smalltv-display-layout.json`에서 함께 관리합니다. 페이지 전환 시간은 장치의 설정 페이지에서 2~60초로 바꿀 수 있습니다. 실행 중인 미리보기는 이 파일의 변경을 자동 반영하고, PlatformIO는 펌웨어를 빌드할 때 같은 파일에서 C++ 헤더를 생성합니다.

```bash
dotnet run --project src/UsageMonitor.SmallTV.Preview/UsageMonitor.SmallTV.Preview.csproj
```

Desktop 앱은 아래 스크립트로 자체 포함 단일 exe(win-x64)를 만듭니다. `-Clean`을 붙이면 이전 출력 폴더를 지우고 새로 만들고, `-OutputDir`로 출력 위치를 바꿀 수 있습니다.

```powershell
.\tools\publish-desktop.ps1
```

스크립트는 다음 명령을 실행하는 것과 같습니다.

```bash
dotnet publish src/UsageMonitor.Desktop/UsageMonitor.Desktop.csproj -c Release -o artifacts/publish/UsageMonitor.Desktop
```

`artifacts\publish\UsageMonitor.Desktop\UsageMonitor.exe` 하나만 복사하면 됩니다. `artifacts/`는 git에 올라가지 않으므로 새로 받은 저장소에서는 다시 만들어야 합니다.

## 요청 제한(HTTP 429)

사용량 엔드포인트가 429를 돌려주면 해당 도구의 조회를 2분 → 5분 → 10분 → 15분 순으로 쉽니다. 마지막 값은 여전히 유효하므로 카드 색과 갱신 시각은 그대로 두고, 아래에 "요청 제한, N분 후 재시도"만 작게 표시합니다(인증 실패·네트워크 오류는 회색 처리). 성공하면 대기 단계가 초기화됩니다. 트레이의 **지금 갱신**은 대기를 무시하고 즉시 조회합니다.

Claude 엔드포인트의 제한은 토큰이 아니라 계정 단위로 보입니다. 같은 계정으로 여러 PC에서 모니터를 돌리거나 Claude Code 세션이 많으면 호출이 합산되므로, 429가 잦으면 Claude 갱신 주기를 5분으로 올리세요.

## 알려진 제약

- 바는 주 모니터에만 표시됩니다.
- 셸이 전체화면 앱(게임, 전체화면 영상)을 알리면 작업표시줄처럼 바를 맨 아래로 내려 그 화면을 가리지 않고, 전체화면이 끝나면 다시 맨 위로 올립니다.
- 동시에 한 인스턴스만 실행됩니다.

## SmallTV-Ultra 연동

`firmware/UsageMonitor.SmallTV`에는 PC 앱과 별개로 동작하는 ESP8266 펌웨어가 있습니다. SmallTV가 Wi-Fi에서 DHCP 주소를 받고 Claude/Codex 사용량 엔드포인트를 HTTPS로 직접 조회합니다. LCD에는 사용률, 리셋까지 남은 시간, 현재 IP를 표시합니다.

SmallTV LCD는 Claude의 5시간, 주간, Fable 주간 한도와 Codex의 주간 한도만 표시합니다. PC용 WPF 위젯의 표시 범위는 그대로 유지합니다.

웹 설정은 정상 Wi-Fi 주소와 복구 AP `192.168.4.1`에서 모두 열립니다. Wi-Fi 검색/수동 입력, 연결 지연, 계정 토큰, 밝기, 야간 모드, 초기화, 재부팅, OTA를 제공합니다. PC UsageMonitor 앱에는 SmallTV용 서버나 설정을 추가하지 않았습니다.

웹 상태·네트워크·계정·설정·펌웨어 업데이트 화면과 저장/오류 응답은 UTF-8 한글로 표시합니다. 진단용 `/status.json` 필드와 LCD 상태 문자열은 기존 호환성을 유지합니다.

웹의 `admin` 로그인은 설정과 OTA를 보호하는 기기 인증이며 Claude/ChatGPT OAuth 로그인과는 별개입니다. SmallTV의 `/accounts.html`은 브라우저 로그인 페이지가 아니라 PC에서 내보낸 OAuth 토큰 묶음을 등록하는 페이지입니다. 등록 후에는 SmallTV가 LittleFS의 refresh token으로 직접 갱신하므로 PC 앱을 실행할 필요가 없습니다.

현재 등록 방식은 PC 토큰을 복사하므로 PC와 SmallTV가 같은 refresh token 값으로 시작할 수 있습니다. 공급자가 토큰 회전 시 이전 값을 무효화하면 한쪽에 재로그인이 필요할 수 있습니다. 인증 오류가 발생하면 PC에서 다시 로그인하고 새 묶음을 SmallTV에 등록하세요. 자세한 저장 위치와 갱신 흐름은 아래 안내에 정리되어 있습니다.

설치, 인증 준비, 웹 화면, 복구 동작과 검증 범위는 [SmallTV 독립형 펌웨어 안내](docs/SMALLTV.md)를 참고하세요.
