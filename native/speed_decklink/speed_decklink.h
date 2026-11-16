// JSPEED DeckLink bridge: a small C API over the Blackmagic DeckLink SDK,
// so the C# app can drive an UltraStudio / DeckLink output on Windows and macOS.
#pragma once

#ifdef _WIN32
#define SPD_API extern "C" __declspec(dllexport)
#else
#define SPD_API extern "C" __attribute__((visibility("default")))
#endif

// Finds DeckLink devices that can output video. Returns the device count,
// or -1 when the Blackmagic Desktop Video driver isn't installed.
SPD_API int spd_init(void);

SPD_API int spd_device_name(int device, char* buffer, int length);

// Display modes the device can output as 8-bit BGRA.
SPD_API int spd_mode_count(int device);
SPD_API int spd_mode_info(int device, int mode, char* name, int nameLength,
                          int* width, int* height, long long* frameDuration,
                          long long* timeScale, unsigned int* modeId);

// Starts scheduled playback. Returns 0 on success, otherwise an error code (see .cpp).
SPD_API int spd_start(int device, unsigned int modeId);

// Hands over the newest frame; it is copied, and repeated until the next one arrives.
SPD_API void spd_submit(const unsigned char* bgra, int width, int height, int rowBytes);

SPD_API void spd_stop(void);

// Frames the hardware reported as late or dropped since spd_start.
SPD_API long long spd_late_frames(void);

SPD_API void spd_shutdown(void);
