#pragma once

#include <cstddef>
#include <cstring>

inline bool isValidWifiSsid(const char* value) {
    size_t length = value == nullptr ? 0 : strlen(value);
    return length >= 1 && length <= 32;
}

inline bool isValidWifiPassword(const char* value) {
    size_t length = value == nullptr ? 0 : strlen(value);
    return length == 0 || (length >= 8 && length <= 63);
}

inline bool isValidWifiDelay(long value) {
    return value >= 0 && value <= 300;
}

inline bool isValidRefreshSeconds(long value) {
    return value >= 60 && value <= 3600;
}

inline bool isValidPageIntervalSeconds(long value) {
    return value >= 2 && value <= 60;
}

inline bool isValidBrightness(long value) {
    return value >= 0 && value <= 255;
}

inline bool isValidHour(long value) {
    return value >= 0 && value <= 23;
}

inline bool isValidUtcOffset(long value) {
    return value >= -12 && value <= 14;
}

inline bool isValidToken(const char* value) {
    if (value == nullptr) return false;
    size_t length = strlen(value);
    if (length == 0) return true;
    if (length > 4096) return false;
    for (size_t i = 0; i < length; i++) {
        unsigned char c = static_cast<unsigned char>(value[i]);
        if (c <= 32 || c >= 127) return false;
    }
    return true;
}
