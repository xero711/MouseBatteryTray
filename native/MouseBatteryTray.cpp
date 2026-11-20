#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <setupapi.h>
#include <cfgmgr32.h>
#include <hidsdi.h>
#include <devioctl.h>
#include <hidclass.h>
#include <hidpi.h>
#include <hidusage.h>
#include <dbt.h>
#include <devpkey.h>
#include <string>
#include <vector>
#include <algorithm>
#include <cwctype>
#include <cmath>
#include <memory>
#include <atomic>

namespace {
constexpr UINT WM_TRAY = WM_APP + 1;
constexpr UINT WM_BATTERY_RESULTS = WM_APP + 2;
constexpr UINT WM_BATTERY_WORKER_FINISHED = WM_APP + 3;
constexpr UINT_PTR TIMER_BATTERY = 1;
constexpr UINT_PTR TIMER_DEBOUNCE = 2;
constexpr UINT_PTR TIMER_WORKER_REAP = 3;
constexpr ULONGLONG kShutdownTimeoutMs = 3000;
constexpr UINT ID_REFRESH = 100;
constexpr UINT ID_STARTUP = 101;
constexpr UINT ID_EXIT = 102;
constexpr UINT ID_COUNT_BASE = 200;
constexpr UINT ID_LANGUAGE_BASE = 120;
constexpr USAGE kBatteryStrengthUsagePage = 0x06;
constexpr USAGE kBatteryStrengthUsage = 0x20;
constexpr wchar_t kAppName[] = L"Mouse Battery Tray";
constexpr wchar_t kRunKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
constexpr wchar_t kRunValue[] = L"MouseBatteryTray";
const GUID kHidInterfaceGuid = { 0x4D1E55B2, 0xF16F, 0x11CF, { 0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30 } };
const DEVPROPKEY kContainerIdKey = { { 0x8C7ED206, 0x3F8A, 0x4827, { 0xB3, 0xAB, 0xAE, 0x9E, 0x1F, 0xAE, 0xFC, 0x6C } }, 2 };
// Windows exposes these byte percentages on supported Bluetooth PnP nodes.
const DEVPROPKEY kSystemBatteryLifeKey = { { 0x49CD1F76, 0x5626, 0x4B17, { 0xA4, 0xE8, 0x18, 0xB4, 0xAA, 0x1A, 0x22, 0x13 } }, 10 };
const DEVPROPKEY kBluetoothBatteryKey = { { 0x104EA319, 0x6EE2, 0x4701, { 0xBD, 0x47, 0x8D, 0xDB, 0xF4, 0x25, 0xBB, 0xE5 } }, 2 };

enum class Language : int { Japanese = 0, English = 1 };
struct Settings { int maxMice = 4; Language language = Language::Japanese; };
struct LogitechRoute {
    BYTE deviceIndex = 0;
    BYTE featureIndex = 0;
    USHORT featureId = 0;
    bool valid = false;
};
struct GenericBatteryFeature {
    USHORT linkCollection = 0;
    BYTE reportId = 0;
    LONG logicalMinimum = 0;
    LONG logicalMaximum = 100;
};
struct GenericBatteryEndpoint {
    std::wstring path;
    std::vector<GenericBatteryFeature> features;
};
struct GenericBatteryCandidate {
    std::wstring identity;
    USHORT vendorId = 0;
    USHORT productId = 0;
    GenericBatteryEndpoint endpoint;
};
struct RazerInterface {
    std::wstring path;
    USAGE usagePage = 0;
    USAGE usage = 0;
};
struct MouseDevice {
    std::wstring key;
    std::wstring name;
    std::wstring path;
    std::wstring containerId;
    std::wstring wiredPath;
    std::wstring receiverPath;
    std::wstring errorPath;
    std::vector<std::wstring> mousePaths;
    std::vector<std::wstring> bluetoothBatteryNodeIds;
    USHORT vendorId = 0;
    USHORT productId = 0;
    int percent = -1;
    int charging = -1;
    std::wstring state;
    LogitechRoute wiredRoute;
    LogitechRoute receiverRoute;
    int razerTid = -1;
    bool razerFeature = false;
    std::wstring razerPath;
    USAGE razerUsagePage = 0;
    USAGE razerUsage = 0;
    std::vector<RazerInterface> razerInterfaces;
    std::vector<GenericBatteryEndpoint> genericBatteryEndpoints;
};
struct BatteryResult {
    std::wstring key;
    int percent = -1;
    int charging = -1;
    std::wstring state;
    LogitechRoute wiredRoute;
    LogitechRoute receiverRoute;
    int razerTid = -1;
    std::wstring razerPath;
};
struct BatteryWorkerRecord {
    DWORD threadId = 0;
    std::wstring key;
    std::vector<std::wstring> paths;
    HANDLE handle{};
};

HINSTANCE g_instance{};
HWND g_window{};
HWND g_panel{};
HDEVNOTIFY g_deviceNotify{};
NOTIFYICONDATAW g_tray{};
Settings g_settings;
std::wstring g_iniPath;
std::vector<MouseDevice> g_mice;
std::vector<BatteryWorkerRecord> g_batteryWorkers;
bool g_shuttingDown = false;
std::atomic<bool> g_stopRequested{ false };
HANDLE g_stopEvent{};
HANDLE g_mutex{};
ULONGLONG g_shutdownDeadline = 0;
bool g_workingSetTrimmed = false;
int g_panelScrollOffset = 0;
int g_panelVisibleRows = 5;
int g_panelWheelRemainder = 0;

std::wstring Text(const wchar_t* ja, const wchar_t* en) {
    return g_settings.language == Language::Japanese ? ja : en;
}

std::wstring SettingsPath() {
    PWSTR localAppData = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT, nullptr, &localAppData))) return L"";
    std::wstring path(localAppData);
    CoTaskMemFree(localAppData);
    path += L"\\MouseBatteryTray\\settings.ini";
    CreateDirectoryW((path.substr(0, path.find_last_of(L'\\'))).c_str(), nullptr);
    return path;
}

void LoadSettings() {
    g_iniPath = SettingsPath();
    if (g_iniPath.empty()) return;
    g_settings.maxMice = std::clamp(static_cast<int>(GetPrivateProfileIntW(L"General", L"MaxMice", 4, g_iniPath.c_str())), 1, 16);
    g_settings.language = GetPrivateProfileIntW(L"General", L"Language", 0, g_iniPath.c_str()) == 1
        ? Language::English : Language::Japanese;
}

void SaveSetting(const wchar_t* key, int value) {
    if (g_iniPath.empty()) return;
    wchar_t text[16]{};
    _itow_s(value, text, 10);
    WritePrivateProfileStringW(L"General", key, text, g_iniPath.c_str());
}

std::wstring Lower(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t c) { return static_cast<wchar_t>(towlower(c)); });
    return value;
}

bool Contains(const std::wstring& value, const wchar_t* part) {
    return Lower(value).find(Lower(part)) != std::wstring::npos;
}

bool LooksLikeKeyboard(const std::wstring& name) {
    return Contains(name, L"keyboard") || Contains(name, L"keypad") || Contains(name, L"huntsman")
        || Contains(name, L"blackwidow") || Contains(name, L"cynosa") || Contains(name, L"ornata");
}

bool IsKnownBatteryMouse(USHORT vid, USHORT pid) {
    if (vid == 0x046D && (pid == 0xC095 || pid == 0xC547)) return true;
    if (vid == 0x3554 && (pid == 0xF50F || pid == 0xF510)) return true;
    if (vid != 0x1532) return false;
    switch (pid) {
    case 0x001F: case 0x0024: case 0x0025: case 0x0032: case 0x003E: case 0x003F:
    case 0x0044: case 0x0045: case 0x0059: case 0x005A: case 0x0062: case 0x006F: case 0x0070:
    case 0x0072: case 0x0073: case 0x0077: case 0x0080: case 0x007A: case 0x007B: case 0x007C:
    case 0x007D: case 0x0083: case 0x0086: case 0x0088: case 0x008F: case 0x0090: case 0x0094:
    case 0x009A: case 0x009C: case 0x009E: case 0x009F: case 0x00A5: case 0x00A6: case 0x00A7:
    case 0x00A8: case 0x00AA: case 0x00AB: case 0x00AF: case 0x00B0: case 0x00B3: case 0x00B4:
    case 0x00B6: case 0x00B7: case 0x00B8: case 0x00B9: case 0x00BE: case 0x00BF: case 0x00C0:
    case 0x00C1: case 0x00C2: case 0x00C3: case 0x00C4: case 0x00C5: case 0x00C7: case 0x00C8:
    case 0x00CC: case 0x00CD: case 0x00D0: case 0x00D1: case 0x00D3: case 0x00D4: case 0x00D6:
    case 0x00D7: case 0x00DE: case 0x00DF: case 0x00E5: case 0x00E6:
        return true;
    default: return false;
    }
}

std::wstring KnownName(USHORT vid, USHORT pid) {
    if (vid == 0x046D) return L"Logitech G502 X PLUS";
    if (vid == 0x3554) return L"LAMZU Maya";
    if (vid != 0x1532) return L"";
    switch (pid) {
    case 0x001F: return L"Razer Naga Epic";
    case 0x0024: case 0x0025: return L"Razer Mamba (2012)";
    case 0x0032: return L"Razer Ouroboros";
    case 0x003E: case 0x003F: return L"Razer Naga Epic Chroma";
    case 0x0044: case 0x0045: return L"Razer Mamba";
    case 0x0059: case 0x005A: return L"Razer Lancehead";
    case 0x0062: return L"Razer Atheris";
    case 0x006F: case 0x0070: return L"Razer Lancehead Wireless";
    case 0x0072: case 0x0073: return L"Razer Mamba Wireless";
    case 0x0077: case 0x0080: return L"Razer Pro Click";
    case 0x007A: case 0x007B: return L"Razer Viper Ultimate";
    case 0x007C: case 0x007D: return L"Razer DeathAdder V2 Pro";
    case 0x0083: return L"Razer Basilisk X HyperSpeed";
    case 0x0086: case 0x0088: return L"Razer Basilisk Ultimate";
    case 0x008F: case 0x0090: return L"Razer Naga Pro";
    case 0x0094: return L"Razer Orochi V2";
    case 0x009A: return L"Razer Pro Click Mini";
    case 0x009C: return L"Razer DeathAdder V2 X HyperSpeed";
    case 0x009E: case 0x009F: return L"Razer Viper Mini SE";
    case 0x00A5: case 0x00A6: return L"Razer Viper V2 Pro";
    case 0x00A7: case 0x00A8: return L"Razer Naga V2 Pro";
    case 0x00AA: case 0x00AB: return L"Razer Basilisk V3 Pro";
    case 0x00AF: case 0x00B0: return L"Razer Cobra Pro";
    case 0x00B3: return L"Razer HyperPolling Dongle";
    case 0x00B4: return L"Razer Naga V2 HyperSpeed";
    case 0x00B6: case 0x00B7: case 0x00C2: case 0x00C3: return L"Razer DeathAdder V3 Pro";
    case 0x00B8: return L"Razer Viper V3 HyperSpeed";
    case 0x00B9: return L"Razer Basilisk V3 X HyperSpeed";
    case 0x00BE: case 0x00BF: return L"Razer DeathAdder V4 Pro";
    case 0x00C0: case 0x00C1: return L"Razer Viper V3 Pro";
    case 0x00C4: case 0x00C5: return L"Razer DeathAdder V3 HyperSpeed";
    case 0x00C7: case 0x00C8: return L"Razer Pro Click V2 Vertical Edition";
    case 0x00CC: case 0x00CD: return L"Razer Basilisk V3 Pro 35K";
    case 0x00D0: case 0x00D1: return L"Razer Pro Click V2";
    case 0x00D3: case 0x00D4: return L"Razer Basilisk Mobile";
    case 0x00D6: case 0x00D7: return L"Razer Basilisk V3 Pro 35K Phantom Green";
    case 0x00DE: case 0x00DF: return L"Razer Viper V3 Pro SE";
    case 0x00E5: case 0x00E6: return L"Razer Viper V4 Pro";
    default: return L"Razer wireless mouse";
    }
}

std::wstring UsageIdentity(const std::wstring& path) {
    auto identity = Lower(path);
    auto end = identity.find(L"#{");
    if (end != std::wstring::npos) identity.resize(end);
    for (size_t pos = identity.find(L"&col"); pos != std::wstring::npos; pos = identity.find(L"&col", pos)) {
        size_t after = pos + 4;
        while (after < identity.size() && iswxdigit(identity[after])) ++after;
        identity.erase(pos, after - pos);
    }
    for (size_t pos = identity.find(L"&mi_"); pos != std::wstring::npos; pos = identity.find(L"&mi_", pos)) {
        const size_t after = std::min(pos + 6, identity.size());
        identity.erase(pos, after - pos);
    }
    return identity;
}

bool IsNullContainerId(const std::wstring& containerId) {
    auto normalized = containerId;
    std::transform(normalized.begin(), normalized.end(), normalized.begin(),
        [](wchar_t c) { return static_cast<wchar_t>(towupper(c)); });
    return normalized == L"{00000000-0000-0000-FFFF-FFFFFFFFFFFF}";
}

std::wstring ContainerIdentity(HDEVINFO set, SP_DEVINFO_DATA& info) {
    GUID container{};
    DEVPROPTYPE type = 0;
    DWORD required = 0;
    if (!SetupDiGetDevicePropertyW(set, &info, &kContainerIdKey, &type,
        reinterpret_cast<PBYTE>(&container), sizeof(container), &required, 0) || type != DEVPROP_TYPE_GUID) return L"";
    wchar_t value[64]{};
    if (!StringFromGUID2(container, value, static_cast<int>(_countof(value)))) return L"";
    return value;
}

std::wstring DeviceInstanceId(HDEVINFO set, SP_DEVINFO_DATA& info) {
    DWORD required = 0;
    SetupDiGetDeviceInstanceIdW(set, &info, nullptr, 0, &required);
    if (!required) return L"";
    std::vector<wchar_t> id(required);
    if (!SetupDiGetDeviceInstanceIdW(set, &info, id.data(), required, nullptr)) return L"";
    return id.data();
}

void AttachBluetoothBatteryNodes(std::vector<MouseDevice>& mice) {
    constexpr const wchar_t* enumerators[] = { L"BTHENUM", L"BTHLE", L"BTHLEDEVICE" };
    for (const auto* enumerator : enumerators) {
        HDEVINFO set = SetupDiGetClassDevsW(nullptr, enumerator, nullptr, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == INVALID_HANDLE_VALUE) continue;
        for (DWORD index = 0;; ++index) {
            SP_DEVINFO_DATA info{ sizeof(info) };
            if (!SetupDiEnumDeviceInfo(set, index, &info)) break;
            const auto containerId = ContainerIdentity(set, info);
            if (containerId.empty() || IsNullContainerId(containerId)) continue;
            const auto mouse = std::find_if(mice.begin(), mice.end(), [&](const MouseDevice& item) {
                return item.containerId == containerId;
            });
            if (mouse == mice.end()) continue;
            auto instanceId = DeviceInstanceId(set, info);
            if (instanceId.empty()) continue;
            if (std::find(mouse->bluetoothBatteryNodeIds.begin(), mouse->bluetoothBatteryNodeIds.end(), instanceId)
                == mouse->bluetoothBatteryNodeIds.end()) mouse->bluetoothBatteryNodeIds.push_back(std::move(instanceId));
        }
        SetupDiDestroyDeviceInfoList(set);
    }
}

std::vector<GenericBatteryFeature> FindBatteryFeatures(PHIDP_PREPARSED_DATA preparsed, const HIDP_CAPS& caps) {
    std::vector<GenericBatteryFeature> result;
    if (!preparsed || caps.NumberFeatureValueCaps == 0) return result;
    std::vector<HIDP_VALUE_CAPS> values(caps.NumberFeatureValueCaps);
    USHORT count = static_cast<USHORT>(values.size());
    if (HidP_GetValueCaps(HidP_Feature, values.data(), &count, preparsed) != HIDP_STATUS_SUCCESS) return result;
    for (USHORT i = 0; i < count; ++i) {
        const auto& value = values[i];
        if (value.UsagePage != kBatteryStrengthUsagePage) continue;
        const bool hasBatteryStrength = value.IsRange
            ? value.Range.UsageMin <= kBatteryStrengthUsage && value.Range.UsageMax >= kBatteryStrengthUsage
            : value.NotRange.Usage == kBatteryStrengthUsage;
        if (hasBatteryStrength) {
            result.push_back({ value.LinkCollection, value.ReportID, value.LogicalMin, value.LogicalMax });
        }
    }
    return result;
}

bool HasFeatureReportId(PHIDP_PREPARSED_DATA preparsed, const HIDP_CAPS& caps, BYTE reportId) {
    if (!preparsed) return false;
    if (caps.NumberFeatureValueCaps > 0) {
        std::vector<HIDP_VALUE_CAPS> values(caps.NumberFeatureValueCaps);
        USHORT count = static_cast<USHORT>(values.size());
        if (HidP_GetValueCaps(HidP_Feature, values.data(), &count, preparsed) == HIDP_STATUS_SUCCESS
            && std::any_of(values.begin(), values.begin() + count, [&](const HIDP_VALUE_CAPS& value) { return value.ReportID == reportId; })) return true;
    }
    if (caps.NumberFeatureButtonCaps > 0) {
        std::vector<HIDP_BUTTON_CAPS> buttons(caps.NumberFeatureButtonCaps);
        USHORT count = static_cast<USHORT>(buttons.size());
        if (HidP_GetButtonCaps(HidP_Feature, buttons.data(), &count, preparsed) == HIDP_STATUS_SUCCESS
            && std::any_of(buttons.begin(), buttons.begin() + count, [&](const HIDP_BUTTON_CAPS& button) { return button.ReportID == reportId; })) return true;
    }
    return false;
}

std::vector<MouseDevice> EnumerateMice() {
    std::vector<MouseDevice> result;
    std::vector<std::pair<std::wstring, std::wstring>> logitechShortPaths;
    std::vector<GenericBatteryCandidate> genericBatteryCandidates;
    std::vector<std::wstring> keyboardContainers;
    HDEVINFO set = SetupDiGetClassDevsW(&kHidInterfaceGuid, nullptr, nullptr, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (set == INVALID_HANDLE_VALUE) return result;

    for (DWORD index = 0;; ++index) {
        SP_DEVICE_INTERFACE_DATA interfaceData{ sizeof(interfaceData) };
        if (!SetupDiEnumDeviceInterfaces(set, nullptr, &kHidInterfaceGuid, index, &interfaceData)) break;
        DWORD needed = 0;
        SetupDiGetDeviceInterfaceDetailW(set, &interfaceData, nullptr, 0, &needed, nullptr);
        if (!needed) continue;
        std::vector<BYTE> storage(needed);
        auto detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(storage.data());
        detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);
        SP_DEVINFO_DATA info{ sizeof(info) };
        if (!SetupDiGetDeviceInterfaceDetailW(set, &interfaceData, detail, needed, nullptr, &info)) continue;
        const auto rawContainerId = ContainerIdentity(set, info);
        const std::wstring containerId = IsNullContainerId(rawContainerId) ? std::wstring{} : rawContainerId;

        HANDLE handle = CreateFileW(detail->DevicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (handle == INVALID_HANDLE_VALUE) continue;
        HIDD_ATTRIBUTES attributes{ sizeof(attributes) };
        if (!HidD_GetAttributes(handle, &attributes)) { CloseHandle(handle); continue; }
        PHIDP_PREPARSED_DATA preparsed = nullptr;
        if (!HidD_GetPreparsedData(handle, &preparsed)) { CloseHandle(handle); continue; }
        HIDP_CAPS caps{};
        const auto status = HidP_GetCaps(preparsed, &caps);
        auto batteryFeatures = status == HIDP_STATUS_SUCCESS ? FindBatteryFeatures(preparsed, caps)
            : std::vector<GenericBatteryFeature>{};
        wchar_t product[256]{};
        HidD_GetProductString(handle, product, sizeof(product));
        const bool known = IsKnownBatteryMouse(attributes.VendorID, attributes.ProductID);
        const bool genericMouse = status == HIDP_STATUS_SUCCESS && caps.UsagePage == 0x01 && caps.Usage == 0x02;
        const std::wstring productName = product[0] ? product : L"HID mouse";
        const bool keyboardInterface = (status == HIDP_STATUS_SUCCESS && caps.UsagePage == 0x01 && caps.Usage == 0x06)
            || LooksLikeKeyboard(productName);
        if (!containerId.empty() && keyboardInterface
            && std::find(keyboardContainers.begin(), keyboardContainers.end(), containerId) == keyboardContainers.end()) {
            keyboardContainers.push_back(containerId);
        }
        if (!batteryFeatures.empty()) {
            genericBatteryCandidates.push_back({
                containerId.empty() ? UsageIdentity(detail->DevicePath) : containerId,
                attributes.VendorID, attributes.ProductID,
                { detail->DevicePath, std::move(batteryFeatures) }
            });
        }
        const bool razerUsage = caps.UsagePage == 0x0001 || caps.UsagePage == 0xFF00;
        const bool razerFeature = attributes.VendorID == 0x1532 && known && razerUsage
            && caps.FeatureReportByteLength >= 91 && HasFeatureReportId(preparsed, caps, 0);
        const bool logitechBattery = attributes.VendorID == 0x046D
            && (attributes.ProductID == 0xC095 || attributes.ProductID == 0xC547)
            && caps.UsagePage == 0xFF00 && caps.Usage == 0x0002 && caps.OutputReportByteLength >= 20;
        const bool lamzuBattery = attributes.VendorID == 0x3554
            && (attributes.ProductID == 0xF50F || attributes.ProductID == 0xF510)
            && caps.UsagePage == 0xFF02 && caps.Usage == 0x0002 && caps.OutputReportByteLength >= 17;
        const bool knownInterface = (attributes.VendorID == 0x1532 && known && razerFeature)
            || logitechBattery || lamzuBattery;

        if (attributes.VendorID == 0x046D && attributes.ProductID == 0xC547
            && caps.UsagePage == 0xFF00 && caps.Usage == 0x0001 && caps.InputReportByteLength == 7) {
            logitechShortPaths.emplace_back(containerId.empty() ? UsageIdentity(detail->DevicePath) : containerId,
                detail->DevicePath);
        }

        if ((knownInterface || genericMouse) && !LooksLikeKeyboard(productName)) {
            MouseDevice mouse;
            mouse.vendorId = attributes.VendorID;
            mouse.productId = attributes.ProductID;
            mouse.path = detail->DevicePath;
            mouse.containerId = containerId;
            mouse.name = known ? KnownName(mouse.vendorId, mouse.productId) : productName;
            mouse.key = containerId.empty() ? UsageIdentity(mouse.path) : containerId;
            mouse.mousePaths.push_back(mouse.path);
            mouse.razerFeature = razerFeature;
            if (razerFeature) {
                mouse.razerUsagePage = caps.UsagePage;
                mouse.razerUsage = caps.Usage;
                mouse.razerInterfaces.push_back({ mouse.path, caps.UsagePage, caps.Usage });
            }
            if (attributes.VendorID == 0x046D && (attributes.ProductID == 0xC095 || attributes.ProductID == 0xC547)) {
                if (attributes.ProductID == 0xC095) {
                    mouse.name = L"G502 X (USB)";
                    if (logitechBattery) mouse.wiredPath = mouse.path;
                } else {
                    mouse.name = L"G502 X (LIGHTSPEED)";
                    if (logitechBattery) mouse.receiverPath = mouse.path;
                }
            } else if (attributes.VendorID == 0x3554 && (attributes.ProductID == 0xF50F || attributes.ProductID == 0xF510)) {
                if (attributes.ProductID == 0xF50F) {
                    mouse.name = L"LAMZU Maya (USB)";
                    if (lamzuBattery) mouse.wiredPath = mouse.path;
                } else {
                    mouse.name = L"LAMZU Maya (receiver)";
                    if (lamzuBattery) mouse.receiverPath = mouse.path;
                }
            }

            const auto duplicate = std::find_if(result.begin(), result.end(), [&](const MouseDevice& item) { return item.key == mouse.key; });
            if (duplicate == result.end()) result.push_back(std::move(mouse));
            else {
                if (std::find(duplicate->mousePaths.begin(), duplicate->mousePaths.end(), mouse.path) == duplicate->mousePaths.end()) {
                    duplicate->mousePaths.push_back(mouse.path);
                }
                if (!mouse.wiredPath.empty()) duplicate->wiredPath = mouse.wiredPath;
                if (!mouse.receiverPath.empty()) duplicate->receiverPath = mouse.receiverPath;
                if (attributes.VendorID == 0x1532 && razerFeature) {
                    duplicate->razerFeature = true;
                    if (std::find_if(duplicate->razerInterfaces.begin(), duplicate->razerInterfaces.end(), [&](const RazerInterface& item) {
                        return item.path == mouse.path;
                    }) == duplicate->razerInterfaces.end()) duplicate->razerInterfaces.push_back({ mouse.path, caps.UsagePage, caps.Usage });
                    const auto currentScore = duplicate->razerUsagePage == 0x0001 || duplicate->razerUsagePage == 0xFF00 ? 0 : 1;
                    const auto newScore = caps.UsagePage == 0x0001 || caps.UsagePage == 0xFF00 ? 0 : 1;
                    if (duplicate->razerUsagePage == 0 || newScore < currentScore
                        || (newScore == currentScore && detail->DevicePath < duplicate->path)) {
                        duplicate->path = mouse.path;
                        duplicate->razerUsagePage = caps.UsagePage;
                        duplicate->razerUsage = caps.Usage;
                    }
                }
            }
        }

        HidD_FreePreparsedData(preparsed);
        CloseHandle(handle);
    }
    SetupDiDestroyDeviceInfoList(set);
    for (auto& mouse : result) {
        const auto identity = mouse.containerId.empty() ? mouse.key : mouse.containerId;
        const bool containerHasKeyboard = !mouse.containerId.empty()
            && std::find(keyboardContainers.begin(), keyboardContainers.end(), mouse.containerId) != keyboardContainers.end();
        for (auto& candidate : genericBatteryCandidates) {
            if (candidate.identity != identity) continue;
            const bool directMouseInterface = std::find(mouse.mousePaths.begin(), mouse.mousePaths.end(), candidate.endpoint.path)
                != mouse.mousePaths.end();
            if (!directMouseInterface) {
                if (mouse.containerId.empty() || containerHasKeyboard
                    || candidate.vendorId != mouse.vendorId || candidate.productId != mouse.productId) continue;
            }
            const bool alreadyAdded = std::any_of(mouse.genericBatteryEndpoints.begin(), mouse.genericBatteryEndpoints.end(),
                [&](const GenericBatteryEndpoint& endpoint) { return endpoint.path == candidate.endpoint.path; });
            if (!alreadyAdded) mouse.genericBatteryEndpoints.push_back(candidate.endpoint);
        }
    }
    for (auto& mouse : result) {
        if (mouse.vendorId != 0x046D || mouse.productId != 0xC547) continue;
        const auto identity = mouse.containerId.empty() ? mouse.key : mouse.containerId;
        const auto shortPath = std::find_if(logitechShortPaths.begin(), logitechShortPaths.end(), [&](const auto& path) {
            return path.first == identity;
        });
        if (shortPath != logitechShortPaths.end()) mouse.errorPath = shortPath->second;
    }
    std::sort(result.begin(), result.end(), [](const MouseDevice& a, const MouseDevice& b) {
        const bool aDedicated = IsKnownBatteryMouse(a.vendorId, a.productId);
        const bool bDedicated = IsKnownBatteryMouse(b.vendorId, b.productId);
        if (aDedicated != bDedicated) return aDedicated;
        if (a.name != b.name) return a.name < b.name;
        return a.key < b.key;
    });
    if (result.size() > static_cast<size_t>(g_settings.maxMice)) result.resize(g_settings.maxMice);
    AttachBluetoothBatteryNodes(result);
    return result;
}

std::wstring BatteryDisplay(const MouseDevice& mouse, bool menuLabel = false) {
    if (mouse.percent < 0) {
        return mouse.state == L"sleeping"
            ? Text(L"スリープ中", L"Sleeping")
            : menuLabel ? Text(L"残量未取得", L"No battery reading")
                        : Text(L"残量方式未対応", L"Battery protocol unavailable");
    }
    std::wstring label = std::to_wstring(mouse.percent) + L"%";
    if (mouse.state == L"sleeping") label += Text(L"（スリープ中）", L" (sleeping)");
    else if (mouse.state == L"cached") label += Text(L"（前回値）", L" (last reading)");
    else if (mouse.state == L"windows") label += Text(L"（Windows報告値）", L" (Windows reported)");
    return label;
}

std::wstring MouseSummary() {
    if (g_mice.empty()) return Text(L"マウス未検出", L"No mice detected");
    if (g_mice.size() == 1) {
        const auto& mouse = g_mice.front();
        return mouse.name + (mouse.percent >= 0 ? L" " + BatteryDisplay(mouse) : L"");
    }
    const auto lowest = std::min_element(g_mice.begin(), g_mice.end(), [](const MouseDevice& a, const MouseDevice& b) {
        const int aPercent = a.percent < 0 ? 101 : a.percent;
        const int bPercent = b.percent < 0 ? 101 : b.percent;
        return aPercent < bPercent;
    });
    std::wstring summary = std::to_wstring(g_mice.size()) + Text(L"台のマウス", L" mice detected");
    if (lowest != g_mice.end() && lowest->percent >= 0) summary += L" · " + BatteryDisplay(*lowest);
    return summary;
}

void UpdatePanelScroll(HWND hwnd) {
    RECT client{};
    GetClientRect(hwnd, &client);
    g_panelVisibleRows = std::max(1, (static_cast<int>(client.bottom) - 36) / 42);
    const int maxOffset = std::max(0, static_cast<int>(g_mice.size()) - g_panelVisibleRows);
    g_panelScrollOffset = std::clamp(g_panelScrollOffset, 0, maxOffset);
    SCROLLINFO info{ sizeof(info) };
    info.fMask = SIF_RANGE | SIF_PAGE | SIF_POS;
    info.nMin = 0;
    info.nMax = std::max(0, static_cast<int>(g_mice.size()) - 1);
    info.nPage = static_cast<UINT>(g_panelVisibleRows);
    info.nPos = g_panelScrollOffset;
    SetScrollInfo(hwnd, SB_VERT, &info, TRUE);
    ShowScrollBar(hwnd, SB_VERT, maxOffset > 0);
}

void UpdateTray() {
    if (!g_tray.hWnd) return;
    const auto summary = MouseSummary();
    wcsncpy_s(g_tray.szTip, summary.c_str(), _TRUNCATE);
    Shell_NotifyIconW(NIM_MODIFY, &g_tray);
}

void ScanDevices() {
    auto next = EnumerateMice();
    for (auto& mouse : next) {
        const auto previous = std::find_if(g_mice.begin(), g_mice.end(), [&](const MouseDevice& item) { return item.key == mouse.key; });
        if (previous != g_mice.end()) {
            mouse.percent = previous->percent;
            mouse.charging = previous->charging;
            mouse.state = previous->state;
            mouse.wiredRoute = previous->wiredRoute;
            mouse.receiverRoute = previous->receiverRoute;
            mouse.razerTid = previous->razerTid;
            mouse.razerPath = previous->razerPath;
        }
    }
    g_mice = std::move(next);
    UpdateTray();
    if (g_panel && IsWindowVisible(g_panel)) {
        UpdatePanelScroll(g_panel);
        InvalidateRect(g_panel, nullptr, TRUE);
    }
}

void TrimWorkingSetOnce() {
    if (g_workingSetTrimmed) return;
    g_workingSetTrimmed = SetProcessWorkingSetSize(GetCurrentProcess(),
        static_cast<SIZE_T>(-1), static_cast<SIZE_T>(-1)) != FALSE;
}

class HidIo {
public:
    HidIo() = default;
    explicit HidIo(const std::wstring& path) { Open(path); }
    ~HidIo() { Close(); }
    HidIo(const HidIo&) = delete;
    HidIo& operator=(const HidIo&) = delete;

    bool Open(const std::wstring& path) {
        Close();
        handle_ = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
        if (handle_ == INVALID_HANDLE_VALUE) {
            handle_ = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
        }
        if (handle_ == INVALID_HANDLE_VALUE) return false;
        event_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!event_) { Close(); return false; }
        return true;
    }
    bool IsOpen() const { return handle_ != INVALID_HANDLE_VALUE && event_ != nullptr; }
    HANDLE NativeHandle() const { return handle_; }
    static constexpr int kTimedOut = -2;
    bool Write(const BYTE* data, DWORD size, DWORD timeoutMs = 250) { return Transfer(true, const_cast<BYTE*>(data), size, timeoutMs) >= 0; }
    int Read(BYTE* data, DWORD size, DWORD timeoutMs) {
        if (g_stopRequested.load(std::memory_order_relaxed)) return -1;
        const int result = Transfer(false, data, size, timeoutMs);
        return result == kTimedOut ? 0 : result;
    }
    int SetFeature(BYTE* data, DWORD size, DWORD timeoutMs = 250) {
        return FeatureTransfer(IOCTL_HID_SET_FEATURE, data, size, nullptr, 0, timeoutMs);
    }
    int GetFeature(BYTE* data, DWORD size, DWORD timeoutMs = 250) {
        return FeatureTransfer(IOCTL_HID_GET_FEATURE, nullptr, 0, data, size, timeoutMs);
    }

private:
    HANDLE handle_ = INVALID_HANDLE_VALUE;
    HANDLE event_{};

    int Transfer(bool write, BYTE* data, DWORD size, DWORD timeoutMs) {
        if (g_stopRequested.load(std::memory_order_relaxed)) return kTimedOut;
        if (!IsOpen()) return -1;
        OVERLAPPED overlapped{};
        overlapped.hEvent = event_;
        ResetEvent(event_);
        DWORD transferred = 0;
        BOOL ok = write
            ? WriteFile(handle_, data, size, &transferred, &overlapped)
            : ReadFile(handle_, data, size, &transferred, &overlapped);
        if (ok) return static_cast<int>(transferred);
        if (GetLastError() != ERROR_IO_PENDING) return -1;
        const HANDLE waitHandles[] = { event_, g_stopEvent };
        const DWORD wait = g_stopEvent
            ? WaitForMultipleObjects(_countof(waitHandles), waitHandles, FALSE, timeoutMs)
            : WaitForSingleObject(event_, timeoutMs);
        if (wait != WAIT_OBJECT_0) {
            CancelIoEx(handle_, &overlapped);
            // Cancellation is asynchronous. Keep both OVERLAPPED and caller buffer
            // alive until the driver has completed the operation.
            GetOverlappedResult(handle_, &overlapped, &transferred, TRUE);
            return wait == WAIT_TIMEOUT || (g_stopEvent && wait == WAIT_OBJECT_0 + 1) ? kTimedOut : -1;
        }
        return GetOverlappedResult(handle_, &overlapped, &transferred, FALSE)
            ? static_cast<int>(transferred) : -1;
    }

    int FeatureTransfer(DWORD controlCode, BYTE* input, DWORD inputSize, BYTE* output, DWORD outputSize, DWORD timeoutMs) {
        if (g_stopRequested.load(std::memory_order_relaxed)) return kTimedOut;
        if (!IsOpen()) return -1;
        OVERLAPPED overlapped{};
        overlapped.hEvent = event_;
        ResetEvent(event_);
        DWORD transferred = 0;
        const BOOL ok = DeviceIoControl(handle_, controlCode, input, inputSize, output, outputSize, &transferred, &overlapped);
        if (ok) return static_cast<int>(transferred);
        if (GetLastError() != ERROR_IO_PENDING) return -1;
        const HANDLE waitHandles[] = { event_, g_stopEvent };
        const DWORD wait = g_stopEvent
            ? WaitForMultipleObjects(_countof(waitHandles), waitHandles, FALSE, timeoutMs)
            : WaitForSingleObject(event_, timeoutMs);
        if (wait != WAIT_OBJECT_0) {
            CancelIoEx(handle_, &overlapped);
            GetOverlappedResult(handle_, &overlapped, &transferred, TRUE);
            return wait == WAIT_TIMEOUT || (g_stopEvent && wait == WAIT_OBJECT_0 + 1) ? kTimedOut : -1;
        }
        return GetOverlappedResult(handle_, &overlapped, &transferred, FALSE)
            ? static_cast<int>(transferred) : -1;
    }

    void Close() {
        if (event_) { CloseHandle(event_); event_ = nullptr; }
        if (handle_ != INVALID_HANDLE_VALUE) { CloseHandle(handle_); handle_ = INVALID_HANDLE_VALUE; }
    }
};

BYTE RazerTransactionId(USHORT pid) {
    switch (pid) {
    case 0x001F: case 0x0024: case 0x0025: case 0x0032: case 0x003E: case 0x003F:
    case 0x0044: case 0x0045: case 0x007A: case 0x007B: case 0x0083: return 0xFF;
    case 0x0059: case 0x005A: case 0x0072: case 0x0073: case 0x007C: case 0x007D: return 0x3F;
    default: return 0x1F;
    }
}

enum class RazerStatus { Success, Sleeping, Unsupported, NoReply, TimedOut, Rejected };
struct RazerReply { RazerStatus status = RazerStatus::NoReply; BYTE value = 0; };

RazerReply RazerQuery(HidIo& io, BYTE transactionId, BYTE command, ULONGLONG deviceDeadline) {
    if (g_stopRequested.load(std::memory_order_relaxed)) return { RazerStatus::NoReply, 0 };
    const ULONGLONG now = GetTickCount64();
    if (now >= deviceDeadline) return { RazerStatus::NoReply, 0 };
    const ULONGLONG deadline = std::min(now + 850, deviceDeadline);
    const auto remainingTimeout = [&]() -> DWORD {
        const ULONGLONG current = GetTickCount64();
        if (current >= deadline || deadline - current < 25) return 0;
        return static_cast<DWORD>(std::min<ULONGLONG>(250, deadline - current));
    };
    BYTE request[91]{};
    request[0] = 0;             // HID report id
    request[1] = 0;             // Razer request status
    request[2] = transactionId;
    request[6] = 0x02;          // payload size
    request[7] = 0x07;          // power command class
    request[8] = command;       // 0x80 battery, 0x84 charging
    BYTE checksum = 0;
    for (int i = 3; i <= 88; ++i) checksum ^= request[i];
    request[89] = checksum;
    DWORD timeout = remainingTimeout();
    if (!timeout) return { RazerStatus::NoReply, 0 };
    const int setResult = io.SetFeature(request, sizeof(request), timeout);
    if (setResult == HidIo::kTimedOut) return { RazerStatus::TimedOut, 0 };
    if (setResult < 0) return { RazerStatus::Rejected, 0 };

    BYTE response[91]{};
    int foreignResponses = 0;
    int sends = 1;
    bool first = true;
    while (GetTickCount64() < deadline) {
        if (g_stopRequested.load(std::memory_order_relaxed)) return { RazerStatus::NoReply, 0 };
        const DWORD delay = std::min<DWORD>(first ? 60 : 75,
            static_cast<DWORD>(std::max<ULONGLONG>(1, deadline - GetTickCount64())));
        if (g_stopEvent) {
            if (WaitForSingleObject(g_stopEvent, delay) == WAIT_OBJECT_0) return { RazerStatus::NoReply, 0 };
        } else Sleep(delay);
        first = false;
        if (g_stopRequested.load(std::memory_order_relaxed)) return { RazerStatus::NoReply, 0 };
        response[0] = 0;
        timeout = remainingTimeout();
        if (!timeout) return { RazerStatus::NoReply, 0 };
        const int getResult = io.GetFeature(response, sizeof(response), timeout);
        if (getResult == HidIo::kTimedOut) return { RazerStatus::TimedOut, 0 };
        if (getResult < 0) return { RazerStatus::Rejected, 0 };
        const bool matches = response[2] == transactionId && response[7] == 0x07 && response[8] == command;
        if (matches) {
            switch (response[1]) {
            case 0x02: return { RazerStatus::Success, response[10] };
            case 0x01: continue;
            case 0x04: return { RazerStatus::Sleeping, 0 };
            case 0x05: return { RazerStatus::Unsupported, 0 };
            default: return { RazerStatus::Rejected, 0 };
            }
        }
        if (response[1] == 0x02 && ++foreignResponses % 4 == 0 && sends < 3) {
            timeout = remainingTimeout();
            if (!timeout) return { RazerStatus::NoReply, 0 };
            const int resendResult = io.SetFeature(request, sizeof(request), timeout);
            if (resendResult == HidIo::kTimedOut) return { RazerStatus::TimedOut, 0 };
            if (resendResult < 0) return { RazerStatus::Rejected, 0 };
            ++sends;
        }
    }
    return { RazerStatus::NoReply, 0 };
}

bool ReadRazer(MouseDevice& mouse, BatteryResult& result) {
    const ULONGLONG deviceDeadline = GetTickCount64() + 1500;
    std::vector<const RazerInterface*> interfaces;
    interfaces.reserve(mouse.razerInterfaces.size());
    for (const auto& deviceInterface : mouse.razerInterfaces) interfaces.push_back(&deviceInterface);
    std::sort(interfaces.begin(), interfaces.end(), [&](const RazerInterface* a, const RazerInterface* b) {
        const bool aCached = a->path == mouse.razerPath;
        const bool bCached = b->path == mouse.razerPath;
        if (aCached != bCached) return aCached;
        const int aRank = a->usagePage == 0x0001 || a->usagePage == 0xFF00 ? 0 : 1;
        const int bRank = b->usagePage == 0x0001 || b->usagePage == 0xFF00 ? 0 : 1;
        if (aRank != bRank) return aRank < bRank;
        if (a->usagePage != b->usagePage) return a->usagePage < b->usagePage;
        if (a->usage != b->usage) return a->usage < b->usage;
        return a->path < b->path;
    });
    BYTE preferred = RazerTransactionId(mouse.productId);
    BYTE tids[] = { mouse.razerTid >= 0 ? static_cast<BYTE>(mouse.razerTid) : preferred,
        preferred, 0x1F, 0x3F, 0xFF, 0x9F, 0x08 };
    for (const auto* deviceInterface : interfaces) {
        HidIo io(deviceInterface->path);
        if (!io.IsOpen()) continue;
        for (size_t i = 0; i < _countof(tids); ++i) {
            if (std::find(tids, tids + i, tids[i]) != tids + i) continue;
            const auto reply = RazerQuery(io, tids[i], 0x80, deviceDeadline);
            if (reply.status == RazerStatus::TimedOut) {
                result = { mouse.key, mouse.percent, mouse.charging, L"sleeping" };
                return false;
            }
            if (reply.status == RazerStatus::Success) {
                mouse.razerPath = deviceInterface->path;
                mouse.razerTid = tids[i];
                const int percent = std::clamp(static_cast<int>(std::lround(reply.value * 100.0 / 255.0)), 0, 100);
                const auto charge = RazerQuery(io, tids[i], 0x84, deviceDeadline);
                const int charging = charge.status == RazerStatus::Success ? (charge.value != 0 ? 1 : 0) : mouse.charging;
                result = { mouse.key, percent, charging, L"connected" };
                return true;
            }
            const bool cachedRequest = deviceInterface->path == mouse.razerPath
                && mouse.razerTid == static_cast<int>(tids[i]);
            if (reply.status == RazerStatus::Sleeping && cachedRequest) {
                result = { mouse.key, mouse.percent, mouse.charging, L"sleeping" };
                return false;
            }
            if (reply.status == RazerStatus::Unsupported && cachedRequest) break;
            if (reply.status == RazerStatus::Rejected) break;
        }
    }
    result = { mouse.key, mouse.percent, mouse.charging, L"sleeping" };
    return false;
}

enum class HidppStatus { Ok, Timeout, HidppError, ReceiverError, IoError };
struct HidppReply { HidppStatus status = HidppStatus::Timeout; BYTE params[16]{}; };

HidppReply HidppRequest(HidIo& longIo, HidIo* shortIo, BYTE slot, BYTE feature, BYTE function, const BYTE* args, size_t argCount) {
    BYTE request[20]{};
    request[0] = 0x11;
    request[1] = slot;
    request[2] = feature;
    request[3] = static_cast<BYTE>(((function & 0x0F) << 4) | 0x0B);
    if (args && argCount) memcpy(request + 4, args, std::min(argCount, sizeof(request) - 4));
    if (!longIo.Write(request, sizeof(request))) return { HidppStatus::IoError, {} };

    const ULONGLONG deadline = GetTickCount64() + 700;
    BYTE longReport[20]{};
    BYTE shortReport[7]{};
    while (GetTickCount64() < deadline) {
        const int length = longIo.Read(longReport, sizeof(longReport), 40);
        if (length < 0) return { HidppStatus::IoError, {} };
        if (length >= 20 && longReport[0] == 0x11 && longReport[1] == slot) {
            if (longReport[2] == feature && longReport[3] == request[3]) {
                HidppReply reply{ HidppStatus::Ok, {} };
                memcpy(reply.params, longReport + 4, sizeof(reply.params));
                return reply;
            }
            if (longReport[2] == 0xFF && longReport[3] == feature && longReport[4] == request[3]) {
                return { HidppStatus::HidppError, {} };
            }
        }
        if (shortIo && shortIo->IsOpen()) {
            const int shortLength = shortIo->Read(shortReport, sizeof(shortReport), 1);
            if (shortLength >= 7 && shortReport[0] == 0x10 && shortReport[1] == slot
                && shortReport[2] == 0x8F && shortReport[3] == feature && shortReport[4] == request[3]) {
                return { HidppStatus::ReceiverError, {} };
            }
        }
    }
    return { HidppStatus::Timeout, {} };
}

bool LogitechGetFeatureIndex(HidIo& io, HidIo* shortIo, BYTE slot, USHORT featureId, BYTE& index, bool& unreachable) {
    const BYTE args[] = { static_cast<BYTE>(featureId >> 8), static_cast<BYTE>(featureId) };
    const auto reply = HidppRequest(io, shortIo, slot, 0, 0, args, sizeof(args));
    unreachable = reply.status != HidppStatus::Ok && reply.status != HidppStatus::HidppError;
    if (reply.status != HidppStatus::Ok || reply.params[0] == 0) return false;
    index = reply.params[0];
    return true;
}

bool LogitechQueryBattery(HidIo& io, HidIo* shortIo, BYTE slot, BYTE featureIndex, USHORT featureId,
    int& percent, int& charging) {
    if (featureId == 0x1004) {
        const auto capabilities = HidppRequest(io, shortIo, slot, featureIndex, 0, nullptr, 0);
        if (capabilities.status != HidppStatus::Ok || (capabilities.params[1] & 0x02) == 0) return false;
        const auto reply = HidppRequest(io, shortIo, slot, featureIndex, 1, nullptr, 0);
        if (reply.status != HidppStatus::Ok || reply.params[0] > 100) return false;
        percent = reply.params[0];
        switch (reply.params[2]) {
        case 0: case 3: charging = 0; break;
        case 1: case 2: case 4: charging = 1; break;
        default: charging = -1; break;
        }
        return true;
    }
    if (featureId == 0x1000) {
        const auto reply = HidppRequest(io, shortIo, slot, featureIndex, 0, nullptr, 0);
        if (reply.status != HidppStatus::Ok || reply.params[0] == 0 || reply.params[0] > 100) return false;
        percent = reply.params[0];
        switch (reply.params[2]) {
        case 0: case 3: charging = 0; break;
        case 1: case 2: case 4: charging = 1; break;
        default: charging = -1; break;
        }
        return true;
    }
    return false;
}

bool LogitechTryTransport(MouseDevice& mouse, bool receiver, int& percent, int& charging) {
    const std::wstring& path = receiver ? mouse.receiverPath : mouse.wiredPath;
    if (path.empty()) return false;
    HidIo longIo(path);
    if (!longIo.IsOpen()) return false;
    HidIo shortIo;
    if (receiver && !mouse.errorPath.empty()) shortIo.Open(mouse.errorPath);
    LogitechRoute& cache = receiver ? mouse.receiverRoute : mouse.wiredRoute;
    HidIo* shortHandle = receiver ? &shortIo : nullptr;
    if (cache.valid) {
        if (LogitechQueryBattery(longIo, shortHandle, cache.deviceIndex, cache.featureIndex, cache.featureId, percent, charging)) return true;
        cache.valid = false;
    }

    const BYTE firstSlot = receiver ? 1 : 0xFF;
    const BYTE lastSlot = receiver ? 6 : 0xFF;
    for (BYTE slot = firstSlot; slot <= lastSlot; ++slot) {
        bool unreachable = false;
        for (USHORT featureId : { static_cast<USHORT>(0x1004), static_cast<USHORT>(0x1000) }) {
            BYTE featureIndex = 0;
            if (!LogitechGetFeatureIndex(longIo, shortHandle, slot, featureId, featureIndex, unreachable)) {
                if (unreachable) break;
                continue;
            }
            if (LogitechQueryBattery(longIo, shortHandle, slot, featureIndex, featureId, percent, charging)) {
                cache = { slot, featureIndex, featureId, true };
                return true;
            }
        }
        if (!receiver) break;
    }
    return false;
}

bool ReadLogitech(MouseDevice& mouse, BatteryResult& result) {
    int percent = -1, charging = -1;
    if (LogitechTryTransport(mouse, false, percent, charging) || LogitechTryTransport(mouse, true, percent, charging)) {
        result = { mouse.key, percent, charging, L"connected" };
        return true;
    }
    result = { mouse.key, mouse.percent, mouse.charging, L"sleeping" };
    return false;
}

BYTE CompxChecksum(const BYTE* data) {
    unsigned sum = 0;
    for (int i = 0; i < 16; ++i) sum += data[i];
    return static_cast<BYTE>((0x55 - sum) & 0xFF);
}

bool LamzuTryTransport(MouseDevice& mouse, bool receiver, int& percent, int& charging) {
    const std::wstring& path = receiver ? mouse.receiverPath : mouse.wiredPath;
    if (path.empty()) return false;
    HidIo io(path);
    if (!io.IsOpen()) return false;
    BYTE request[17]{};
    request[0] = 0x08;
    request[1] = 0x04;
    request[16] = CompxChecksum(request);
    if (!io.Write(request, sizeof(request))) return false;
    const ULONGLONG deadline = GetTickCount64() + 600;
    BYTE response[32]{};
    while (GetTickCount64() < deadline) {
        const int length = io.Read(response, sizeof(response), 50);
        if (length < 0) return false;
        if (length < 17 || response[0] != 0x08 || response[1] != 0x04) continue;
        if (response[16] != CompxChecksum(response) || response[6] > 100) continue;
        percent = response[6];
        charging = response[7] ? 1 : 0;
        return true;
    }
    return false;
}

bool ReadLamzu(MouseDevice& mouse, BatteryResult& result) {
    int percent = -1, charging = -1;
    if (LamzuTryTransport(mouse, false, percent, charging) || LamzuTryTransport(mouse, true, percent, charging)) {
        result = { mouse.key, percent, charging, L"connected" };
        return true;
    }
    result = { mouse.key, mouse.percent, mouse.charging, L"sleeping" };
    return false;
}

bool ReadGenericBatteryFeature(MouseDevice& mouse, BatteryResult& result) {
    for (const auto& endpoint : mouse.genericBatteryEndpoints) {
        HidIo io(endpoint.path);
        if (!io.IsOpen()) continue;
        HANDLE handle = io.NativeHandle();

        PHIDP_PREPARSED_DATA preparsed = nullptr;
        HIDD_ATTRIBUTES attributes{ sizeof(attributes) };
        HIDP_CAPS caps{};
        bool success = false;
        bool timedOut = false;
        if (HidD_GetAttributes(handle, &attributes) && HidD_GetPreparsedData(handle, &preparsed)
            && HidP_GetCaps(preparsed, &caps) == HIDP_STATUS_SUCCESS && caps.FeatureReportByteLength > 0) {
            std::vector<BYTE> report(caps.FeatureReportByteLength);
            for (const auto& feature : endpoint.features) {
                std::fill(report.begin(), report.end(), static_cast<BYTE>(0));
                report[0] = feature.reportId;
                const int reportLength = io.GetFeature(report.data(), static_cast<DWORD>(report.size()), 1500);
                if (reportLength == HidIo::kTimedOut) { timedOut = true; break; }
                if (reportLength <= 0) continue;
                ULONG raw = 0;
                if (HidP_GetUsageValue(HidP_Feature, kBatteryStrengthUsagePage, feature.linkCollection, kBatteryStrengthUsage, &raw, preparsed,
                    reinterpret_cast<PCHAR>(report.data()), static_cast<ULONG>(reportLength)) != HIDP_STATUS_SUCCESS) continue;
                if (feature.logicalMaximum <= feature.logicalMinimum) continue;
                const int64_t value = feature.logicalMinimum < 0 ? static_cast<LONG>(raw) : static_cast<int64_t>(raw);
                if (value < feature.logicalMinimum || value > feature.logicalMaximum) continue;
                const double percentValue = feature.logicalMinimum >= 0 && feature.logicalMaximum <= 100
                    ? static_cast<double>(value)
                    : (static_cast<double>(value - feature.logicalMinimum) * 100.0)
                        / static_cast<double>(feature.logicalMaximum - feature.logicalMinimum);
                const int percent = static_cast<int>(std::lround(percentValue));
                if (percent < 0 || percent > 100) continue;
                result = { mouse.key, percent, -1, L"connected" };
                success = true;
                break;
            }
        }
        if (preparsed) HidD_FreePreparsedData(preparsed);
        if (success) return true;
        if (timedOut) return false;
    }
    return false;
}

bool ReadWindowsBluetoothBattery(const MouseDevice& mouse, BatteryResult& result) {
    for (const auto& nodeId : mouse.bluetoothBatteryNodeIds) {
        auto mutableId = nodeId;
        DEVINST deviceInstance{};
        if (CM_Locate_DevNodeW(&deviceInstance, mutableId.data(), CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) continue;
        ULONG nodeStatus = 0;
        ULONG problem = 0;
        if (CM_Get_DevNode_Status(&nodeStatus, &problem, deviceInstance, 0) != CR_SUCCESS
            || (nodeStatus & DN_DEVICE_DISCONNECTED) != 0) continue;
        for (const DEVPROPKEY* key : { &kSystemBatteryLifeKey, &kBluetoothBatteryKey }) {
            BYTE value = 0;
            DEVPROPTYPE type = 0;
            ULONG size = sizeof(value);
            if (CM_Get_DevNode_PropertyW(deviceInstance, key, &type, &value, &size, 0) != CR_SUCCESS
                || type != DEVPROP_TYPE_BYTE || size != sizeof(value) || value > 100) continue;
            result = { mouse.key, value, -1, L"windows" };
            return true;
        }
    }
    return false;
}

DWORD WINAPI BatteryWorker(void* parameter) {
    std::unique_ptr<std::vector<MouseDevice>> snapshot(static_cast<std::vector<MouseDevice>*>(parameter));
    auto* results = new std::vector<BatteryResult>();
    results->reserve(snapshot->size());
    for (auto& mouse : *snapshot) {
        BatteryResult result{ mouse.key, mouse.percent, mouse.charging, L"unsupported" };
        bool readSuccess = false;
        if (mouse.vendorId == 0x1532 && mouse.razerFeature && IsKnownBatteryMouse(mouse.vendorId, mouse.productId)) readSuccess = ReadRazer(mouse, result);
        else if (mouse.vendorId == 0x046D && (mouse.productId == 0xC095 || mouse.productId == 0xC547)) readSuccess = ReadLogitech(mouse, result);
        else if (mouse.vendorId == 0x3554 && (mouse.productId == 0xF50F || mouse.productId == 0xF510)) readSuccess = ReadLamzu(mouse, result);
        if (!readSuccess) readSuccess = ReadGenericBatteryFeature(mouse, result);
        if (!readSuccess) readSuccess = ReadWindowsBluetoothBattery(mouse, result);
        if (!readSuccess && result.percent >= 0 && result.state == L"unsupported") result.state = L"cached";
        result.wiredRoute = mouse.wiredRoute;
        result.receiverRoute = mouse.receiverRoute;
        result.razerTid = mouse.razerTid;
        result.razerPath = mouse.razerPath;
        results->push_back(std::move(result));
    }
    if (!PostMessageW(g_window, WM_BATTERY_RESULTS, 0, reinterpret_cast<LPARAM>(results))) delete results;
    PostMessageW(g_window, WM_BATTERY_WORKER_FINISHED, GetCurrentThreadId(), 0);
    return 0;
}

std::vector<std::wstring> PollingPaths(const MouseDevice& mouse) {
    std::vector<std::wstring> paths;
    auto add = [&](const std::wstring& path) {
        if (!path.empty() && std::find(paths.begin(), paths.end(), path) == paths.end()) paths.push_back(path);
    };
    add(mouse.path);
    for (const auto& path : mouse.mousePaths) add(path);
    add(mouse.wiredPath);
    add(mouse.receiverPath);
    add(mouse.errorPath);
    for (const auto& path : mouse.razerInterfaces) add(path.path);
    for (const auto& endpoint : mouse.genericBatteryEndpoints) add(endpoint.path);
    return paths;
}

bool SharesPollingPath(const std::vector<std::wstring>& a, const std::vector<std::wstring>& b) {
    for (const auto& path : a) if (std::find(b.begin(), b.end(), path) != b.end()) return true;
    return false;
}

void PollBatteries() {
    if (g_shuttingDown) return;
    for (const auto& mouse : g_mice) {
        auto paths = PollingPaths(mouse);
        const bool alreadyPolling = std::any_of(g_batteryWorkers.begin(), g_batteryWorkers.end(), [&](const BatteryWorkerRecord& worker) {
            return worker.key == mouse.key || SharesPollingPath(paths, worker.paths);
        });
        if (alreadyPolling) continue;
        auto* snapshot = new std::vector<MouseDevice>{ mouse };
        g_batteryWorkers.push_back({ 0, mouse.key, std::move(paths), nullptr });
        auto& record = g_batteryWorkers.back();
        HANDLE worker = CreateThread(nullptr, 0, BatteryWorker, snapshot, 0, &record.threadId);
        if (worker) {
            record.handle = worker;
            SetTimer(g_window, TIMER_WORKER_REAP, 250, nullptr);
        }
        else {
            delete snapshot;
            g_batteryWorkers.pop_back();
        }
    }
}

bool ReapFinishedBatteryWorkers(HWND hwnd) {
    for (auto it = g_batteryWorkers.begin(); it != g_batteryWorkers.end();) {
        if (it->handle && WaitForSingleObject(it->handle, 0) == WAIT_OBJECT_0) {
            CloseHandle(it->handle);
            it = g_batteryWorkers.erase(it);
        } else ++it;
    }
    if (!g_batteryWorkers.empty()) return false;
    KillTimer(hwnd, TIMER_WORKER_REAP);
    if (g_shuttingDown) DestroyWindow(hwnd);
    return true;
}

void BeginShutdown(HWND hwnd) {
    if (g_shuttingDown) return;
    g_shuttingDown = true;
    g_stopRequested.store(true, std::memory_order_relaxed);
    g_shutdownDeadline = GetTickCount64() + kShutdownTimeoutMs;
    if (g_stopEvent) SetEvent(g_stopEvent);
    KillTimer(hwnd, TIMER_BATTERY);
    KillTimer(hwnd, TIMER_DEBOUNCE);
    if (g_deviceNotify) {
        UnregisterDeviceNotification(g_deviceNotify);
        g_deviceNotify = nullptr;
    }
    Shell_NotifyIconW(NIM_DELETE, &g_tray);
    if (g_panel) {
        DestroyWindow(g_panel);
        g_panel = nullptr;
    }
    if (g_batteryWorkers.empty()) DestroyWindow(hwnd);
    else SetTimer(hwnd, TIMER_WORKER_REAP, 250, nullptr);
}

bool StartupEnabled() {
    HKEY key{};
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kRunKey, 0, KEY_QUERY_VALUE, &key) != ERROR_SUCCESS) return false;
    DWORD type = 0;
    DWORD size = 0;
    const auto status = RegQueryValueExW(key, kRunValue, nullptr, &type, nullptr, &size);
    RegCloseKey(key);
    return status == ERROR_SUCCESS && (type == REG_SZ || type == REG_EXPAND_SZ) && size > sizeof(wchar_t);
}

bool SetStartup(bool enabled) {
    HKEY key{};
    LONG status = RegCreateKeyExW(HKEY_CURRENT_USER, kRunKey, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr);
    if (status != ERROR_SUCCESS) goto report_error;
    if (enabled) {
        wchar_t exe[MAX_PATH]{};
        if (!GetModuleFileNameW(nullptr, exe, MAX_PATH)) status = GetLastError();
        else {
            std::wstring value = L"\"" + std::wstring(exe) + L"\" --tray";
            status = RegSetValueExW(key, kRunValue, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()),
                static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
        }
    } else {
        status = RegDeleteValueW(key, kRunValue);
        if (status == ERROR_FILE_NOT_FOUND) status = ERROR_SUCCESS;
    }
    RegCloseKey(key);
    if (status == ERROR_SUCCESS) return true;

report_error: {
        wchar_t systemMessage[256]{};
        FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, nullptr,
            static_cast<DWORD>(status), 0, systemMessage, static_cast<DWORD>(_countof(systemMessage)), nullptr);
        std::wstring message = Text(L"Windows起動時の開始設定を保存できませんでした。\n", L"Could not save the startup setting.\n");
        message += systemMessage[0] ? systemMessage : std::to_wstring(status);
        MessageBoxW(nullptr, message.c_str(), kAppName, MB_OK | MB_ICONWARNING);
        return false;
    }
}

void OpenPanel() {
    if (!g_panel) {
        WNDCLASSW wc{};
        wc.lpfnWndProc = [](HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) -> LRESULT {
            if (msg == WM_PAINT) {
                PAINTSTRUCT ps{};
                HDC dc = BeginPaint(hwnd, &ps);
                RECT rect{}; GetClientRect(hwnd, &rect);
                SetBkMode(dc, TRANSPARENT);
                SetTextColor(dc, RGB(255, 255, 255));
                DrawTextW(dc, Text(L"マウス バッテリー", L"Mouse Battery").c_str(), -1, &rect, DT_CENTER | DT_TOP | DT_SINGLELINE);
                rect.top += 36;
                if (g_mice.empty()) {
                    DrawTextW(dc, Text(L"マウスが検出されていません", L"No mice detected").c_str(), -1, &rect, DT_CENTER | DT_TOP | DT_SINGLELINE);
                } else {
                    for (size_t i = static_cast<size_t>(g_panelScrollOffset); i < g_mice.size(); ++i) {
                        RECT row = rect;
                        row.top = 36 + static_cast<int>(i - static_cast<size_t>(g_panelScrollOffset)) * 42;
                        row.bottom = row.top + 42;
                        if (row.top >= rect.bottom) break;
                        std::wstring line = g_mice[i].name + L"    " + BatteryDisplay(g_mice[i]);
                        DrawTextW(dc, line.c_str(), -1, &row, DT_LEFT | DT_TOP | DT_SINGLELINE | DT_END_ELLIPSIS);
                    }
                }
                EndPaint(hwnd, &ps);
                return 0;
            }
            if (msg == WM_ACTIVATE && LOWORD(wp) == WA_INACTIVE) { ShowWindow(hwnd, SW_HIDE); return 0; }
            if (msg == WM_KEYDOWN && wp == VK_ESCAPE) { ShowWindow(hwnd, SW_HIDE); return 0; }
            if (msg == WM_VSCROLL) {
                int next = g_panelScrollOffset;
                switch (LOWORD(wp)) {
                case SB_LINEUP: --next; break;
                case SB_LINEDOWN: ++next; break;
                case SB_PAGEUP: next -= g_panelVisibleRows; break;
                case SB_PAGEDOWN: next += g_panelVisibleRows; break;
                case SB_TOP: next = 0; break;
                case SB_BOTTOM: next = static_cast<int>(g_mice.size()); break;
                case SB_THUMBPOSITION:
                case SB_THUMBTRACK: {
                    SCROLLINFO info{ sizeof(info) };
                    info.fMask = SIF_TRACKPOS;
                    GetScrollInfo(hwnd, SB_VERT, &info);
                    next = info.nTrackPos;
                    break;
                }
                default: return 0;
                }
                g_panelScrollOffset = next;
                UpdatePanelScroll(hwnd);
                InvalidateRect(hwnd, nullptr, TRUE);
                return 0;
            }
            if (msg == WM_MOUSEWHEEL) {
                g_panelWheelRemainder += GET_WHEEL_DELTA_WPARAM(wp);
                const int notches = g_panelWheelRemainder / WHEEL_DELTA;
                g_panelWheelRemainder -= notches * WHEEL_DELTA;
                if (notches != 0) {
                    UINT scrollLines = 3;
                    SystemParametersInfoW(SPI_GETWHEELSCROLLLINES, 0, &scrollLines, 0);
                    if (scrollLines == WHEEL_PAGESCROLL) scrollLines = static_cast<UINT>(g_panelVisibleRows);
                    const int rows = std::min(static_cast<int>(scrollLines), g_panelVisibleRows);
                    g_panelScrollOffset -= notches * rows;
                    UpdatePanelScroll(hwnd);
                    InvalidateRect(hwnd, nullptr, TRUE);
                }
                return 0;
            }
            if (msg == WM_SIZE) {
                UpdatePanelScroll(hwnd);
                return 0;
            }
            return DefWindowProcW(hwnd, msg, wp, lp);
        };
        wc.hInstance = g_instance;
        wc.lpszClassName = L"MouseBatteryTray.NativePanel";
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.hbrBackground = reinterpret_cast<HBRUSH>(GetStockObject(DKGRAY_BRUSH));
        RegisterClassW(&wc);
        g_panel = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, wc.lpszClassName, kAppName,
            WS_POPUP | WS_BORDER | WS_VSCROLL, 0, 0, 520, 260, nullptr, nullptr, g_instance, nullptr);
    }
    POINT point{}; GetCursorPos(&point);
    g_panelScrollOffset = 0;
    g_panelWheelRemainder = 0;
    MONITORINFO monitor{ sizeof(monitor) };
    RECT workArea{ 0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN) };
    if (GetMonitorInfoW(MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST), &monitor)) workArea = monitor.rcWork;
    const int left = static_cast<int>(workArea.left);
    const int top = static_cast<int>(workArea.top);
    const int right = static_cast<int>(workArea.right);
    const int bottom = static_cast<int>(workArea.bottom);
    const int width = std::min(520, std::max(1, right - left));
    const int desiredHeight = std::max(260, 48 + static_cast<int>(g_mice.size()) * 42);
    const int height = std::min(desiredHeight, std::max(1, bottom - top));
    const int x = std::clamp(static_cast<int>(point.x) - width / 2, left, std::max(left, right - width));
    int y = static_cast<int>(point.y) - height - 8;
    if (y < top) y = static_cast<int>(point.y) + 8;
    y = std::clamp(y, top, std::max(top, bottom - height));
    SetWindowPos(g_panel, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
    UpdatePanelScroll(g_panel);
    ShowWindow(g_panel, SW_SHOWNOACTIVATE);
    InvalidateRect(g_panel, nullptr, TRUE);
}

void AppendCountMenu(HMENU menu) {
    HMENU sub = CreatePopupMenu();
    for (int count = 1; count <= 16; ++count) {
        std::wstring label = std::to_wstring(count) + Text(L"台", count == 1 ? L" mouse" : L" mice");
        AppendMenuW(sub, MF_STRING | (g_settings.maxMice == count ? MF_CHECKED : 0), ID_COUNT_BASE + count, label.c_str());
    }
    AppendMenuW(menu, MF_POPUP, reinterpret_cast<UINT_PTR>(sub), Text(L"検出するマウス数", L"Maximum mice to detect").c_str());
}

void AppendLanguageMenu(HMENU menu) {
    HMENU sub = CreatePopupMenu();
    AppendMenuW(sub, MF_STRING | (g_settings.language == Language::Japanese ? MF_CHECKED : 0), ID_LANGUAGE_BASE, L"日本語");
    AppendMenuW(sub, MF_STRING | (g_settings.language == Language::English ? MF_CHECKED : 0), ID_LANGUAGE_BASE + 1, L"English");
    AppendMenuW(menu, MF_POPUP, reinterpret_cast<UINT_PTR>(sub), Text(L"言語 / Language", L"Language / 言語").c_str());
}

void ShowContextMenu() {
    HMENU menu = CreatePopupMenu();
    for (const auto& mouse : g_mice) {
        std::wstring line = mouse.name + L"  " + BatteryDisplay(mouse, true);
        AppendMenuW(menu, MF_STRING | MF_DISABLED | MF_GRAYED, 0, line.c_str());
    }
    if (!g_mice.empty()) AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, ID_REFRESH, Text(L"今すぐ更新", L"Refresh now").c_str());
    AppendCountMenu(menu);
    AppendLanguageMenu(menu);
    AppendMenuW(menu, MF_STRING | (StartupEnabled() ? MF_CHECKED : 0), ID_STARTUP, Text(L"Windows 起動時に開始", L"Start with Windows").c_str());
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, ID_EXIT, Text(L"終了", L"Exit").c_str());
    POINT point{}; GetCursorPos(&point);
    SetForegroundWindow(g_window);
    TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_LEFTALIGN, point.x, point.y, 0, g_window, nullptr);
    DestroyMenu(menu);
}

LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
    switch (message) {
    case WM_CLOSE:
        BeginShutdown(hwnd);
        return 0;
    case WM_CREATE: {
        g_tray = {};
        g_tray.cbSize = sizeof(g_tray);
        g_tray.hWnd = hwnd;
        g_tray.uID = 1;
        g_tray.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        g_tray.uCallbackMessage = WM_TRAY;
        g_tray.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
        wcsncpy_s(g_tray.szTip, kAppName, _TRUNCATE);
        Shell_NotifyIconW(NIM_ADD, &g_tray);
        g_tray.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &g_tray);
        DEV_BROADCAST_DEVICEINTERFACE_W filter{};
        filter.dbcc_size = sizeof(filter);
        filter.dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE;
        filter.dbcc_classguid = kHidInterfaceGuid;
        g_deviceNotify = RegisterDeviceNotificationW(hwnd, &filter, DEVICE_NOTIFY_WINDOW_HANDLE);
        SetTimer(hwnd, TIMER_BATTERY, 1000, nullptr);
        ScanDevices();
        TrimWorkingSetOnce();
        return 0;
    }
    case WM_DEVICECHANGE:
        if (g_shuttingDown) return TRUE;
        if (wParam == DBT_DEVICEARRIVAL || wParam == DBT_DEVICEREMOVECOMPLETE || wParam == DBT_DEVNODES_CHANGED) {
            KillTimer(hwnd, TIMER_DEBOUNCE);
            SetTimer(hwnd, TIMER_DEBOUNCE, 250, nullptr);
        }
        return TRUE;
    case WM_TIMER:
        if (wParam == TIMER_WORKER_REAP) {
            const bool allDone = ReapFinishedBatteryWorkers(hwnd);
            if (!allDone && g_shuttingDown && GetTickCount64() >= g_shutdownDeadline) {
                // A misbehaving HID driver may ignore cancellation forever. The
                // user explicitly requested exit, so don't leave an invisible
                // process holding the single-instance mutex indefinitely.
                if (g_mutex) {
                    ReleaseMutex(g_mutex);
                    CloseHandle(g_mutex);
                    g_mutex = nullptr;
                }
                TerminateProcess(GetCurrentProcess(), 0);
            }
        }
        else if (!g_shuttingDown) {
            if (wParam == TIMER_BATTERY) PollBatteries();
            else if (wParam == TIMER_DEBOUNCE) { KillTimer(hwnd, TIMER_DEBOUNCE); ScanDevices(); }
        }
        return 0;
    case WM_BATTERY_RESULTS: {
        std::unique_ptr<std::vector<BatteryResult>> results(reinterpret_cast<std::vector<BatteryResult>*>(lParam));
        if (g_shuttingDown) return 0;
        for (const auto& result : *results) {
            const auto mouse = std::find_if(g_mice.begin(), g_mice.end(), [&](const MouseDevice& item) { return item.key == result.key; });
            if (mouse != g_mice.end()) {
                mouse->percent = result.percent;
                mouse->charging = result.charging;
                mouse->state = result.state;
                mouse->wiredRoute = result.wiredRoute;
                mouse->receiverRoute = result.receiverRoute;
                mouse->razerTid = result.razerTid;
                mouse->razerPath = result.razerPath;
            }
        }
        UpdateTray();
        if (g_panel && IsWindowVisible(g_panel)) InvalidateRect(g_panel, nullptr, TRUE);
        return 0;
    }
    case WM_BATTERY_WORKER_FINISHED: {
        ReapFinishedBatteryWorkers(hwnd);
        return 0;
    }
    case WM_TRAY:
        if (LOWORD(lParam) == WM_LBUTTONUP || LOWORD(lParam) == NIN_SELECT) OpenPanel();
        else if (LOWORD(lParam) == WM_RBUTTONUP || LOWORD(lParam) == WM_CONTEXTMENU) ShowContextMenu();
        return 0;
    case WM_COMMAND: {
        const UINT id = LOWORD(wParam);
        if (id == ID_REFRESH) ScanDevices();
        else if (id == ID_STARTUP) SetStartup(!StartupEnabled());
        else if (id == ID_EXIT) BeginShutdown(hwnd);
        else if (id >= ID_COUNT_BASE + 1 && id <= ID_COUNT_BASE + 16) {
            g_settings.maxMice = static_cast<int>(id - ID_COUNT_BASE);
            SaveSetting(L"MaxMice", g_settings.maxMice);
            ScanDevices();
        } else if (id == ID_LANGUAGE_BASE || id == ID_LANGUAGE_BASE + 1) {
            g_settings.language = id == ID_LANGUAGE_BASE ? Language::Japanese : Language::English;
            SaveSetting(L"Language", static_cast<int>(g_settings.language));
            UpdateTray();
            if (g_panel) InvalidateRect(g_panel, nullptr, TRUE);
        }
        return 0;
    }
    case WM_DESTROY:
        KillTimer(hwnd, TIMER_BATTERY);
        KillTimer(hwnd, TIMER_WORKER_REAP);
        if (g_deviceNotify) {
            UnregisterDeviceNotification(g_deviceNotify);
            g_deviceNotify = nullptr;
        }
        Shell_NotifyIconW(NIM_DELETE, &g_tray);
        if (g_panel) {
            DestroyWindow(g_panel);
            g_panel = nullptr;
        }
        PostQuitMessage(0);
        return 0;
    default: return DefWindowProcW(hwnd, message, wParam, lParam);
    }
}
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    g_instance = instance;
    LoadSettings();
    SetLastError(ERROR_SUCCESS);
    g_mutex = CreateMutexW(nullptr, TRUE, L"Local\\MouseBatteryTray.Native");
    if (!g_mutex || GetLastError() == ERROR_ALREADY_EXISTS) {
        if (g_mutex) CloseHandle(g_mutex);
        g_mutex = nullptr;
        return 0;
    }
    g_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_stopEvent) { CloseHandle(g_mutex); g_mutex = nullptr; return 1; }
    WNDCLASSW wc{};
    wc.lpfnWndProc = WindowProc;
    wc.hInstance = instance;
    wc.lpszClassName = L"MouseBatteryTray.Native.Main";
    RegisterClassW(&wc);
    g_window = CreateWindowExW(0, wc.lpszClassName, kAppName, WS_OVERLAPPED,
        0, 0, 0, 0, nullptr, nullptr, instance, nullptr);
    if (!g_window) { CloseHandle(g_stopEvent); CloseHandle(g_mutex); g_stopEvent = nullptr; g_mutex = nullptr; return 1; }
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    CloseHandle(g_stopEvent);
    g_stopEvent = nullptr;
    if (g_mutex) {
        ReleaseMutex(g_mutex);
        CloseHandle(g_mutex);
        g_mutex = nullptr;
    }
    return static_cast<int>(message.wParam);
}
