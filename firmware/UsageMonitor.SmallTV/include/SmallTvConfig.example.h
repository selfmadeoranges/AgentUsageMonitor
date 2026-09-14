#pragma once

// Copy to SmallTvConfig.h and fill locally. It is excluded from source control.
// An empty SSID starts the recovery access point immediately.
constexpr char WIFI_SSID[] = "";
constexpr char WIFI_PASSWORD[] = "";
// Optional stable web setup/OTA password (12-31 chars). Empty derives a per-device password shown on the LCD.
constexpr char ADMIN_PASSWORD[] = "";
constexpr uint8_t LCD_ROTATION = 0;
constexpr bool LCD_INVERT = true;
constexpr uint32_t PAGE_INTERVAL_MS = 6000;
