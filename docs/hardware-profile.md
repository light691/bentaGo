# Hardware profile

**Status:** Standard-market selection accepted for the build (operator decision 2026-08-29). Client confirmation pending; real-device verification still gates BG-007 release lanes. Every device goes through an abstraction and a fake adapter; nothing in the app may depend on a specific model.

## Selected standard devices (Philippine market)

| Role | Recommended device | Why | Interface |
|---|---|---|---|
| Barcode scanner | Zebra DS2208 (or DS2208-SR) | The de-facto standard HID scanner in PH retail; 2D + 1D, wide COD support, works as keyboard wedge, zero driver needed | USB keyboard-HID |
| Receipt printer | Epson TM-T82 (or TM-T88V) | Dominant ESC/POS 80mm model in PH stores and sari-sari retail; extremely widely supported; RJ11 cash-drawer kick | USB or LAN (ESC/POS raw) |
| Paper | 80 mm thermal, 203 dpi | The market standard | - |
| Cash drawer (optional) | RJ11 kick driven by the printer | Common in PH stores; MVP keeps this out of scope, drawer kick command only behind the printer profile | RJ11 via printer |

## Contract for the app (independent of model)

- Scanner sends data as keyboard text plus Enter. No suffix filtering by the app at the boundary: **scanner diagnostics show raw input including the exact suffix untrimmed** (BG-007 lane 5). Committing model default suffix in PH market: CR (`\r`) from the DS2208 defaults, configurable in the tool.
- Printer profile: ESC/POS 80 mm, 203 dpi, 42-column nominal receipt; UTF-8 capable; cut command at end of receipt; drawer kick command optional and configurable.
- Barcode formats: alphanumeric; leading zeros significant; lengths between 6 and 20; no auto-padding.
- Display targets: 1366 by 768 and 1920 by 1080; test at 125 percent scaling (BG-004 lane 10).

## Known device facts

| Item | Value | Status |
|---|---|---|
| Scanner suffix | Unknown until a real device runs diagnostics; DS2208 default is CR | To confirm on device |
| Printer media | 80 mm, 203 dpi | Standard |
| Printer cut | Auto-cut where model supports; other models fall back to feed | Standard |
