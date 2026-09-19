// SimpleWallpaper shell extension.
//
// Implements IExplorerCommand for the desktop background verb "Next Wallpaper". While the next
// picture is still being downloaded the command reports ECS_DISABLED, which is what makes Explorer
// draw the entry greyed out and refuse to run it - the entry stays visible, unlike LegacyDisable.
//
// Built as a native x64 in-process COM server so Explorer can load it directly. It deliberately
// does no more than read one registry value and test one file, because it runs inside explorer.exe.

#include <windows.h>
#include <objbase.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <strsafe.h>
#include <stdarg.h>
#include <new>

// {7A2E5C31-9B4D-4E8A-9F2C-3D5E7A1B8C40}
static const CLSID CLSID_SimpleWallpaperCommand =
    { 0x7a2e5c31, 0x9b4d, 0x4e8a, { 0x9f, 0x2c, 0x3d, 0x5e, 0x7a, 0x1b, 0x8c, 0x40 } };

static const wchar_t* kFlagFileName = L"Wallpaper\\next-ready.flag";
static const wchar_t* kLogFileName = L"Wallpaper\\shellext.log";
static const wchar_t* kExecutableName = L"SimpleWallpaper.exe";

static HMODULE g_thisModule = nullptr;

// A shell extension that fails silently cannot be diagnosed, so every decision is appended to
// %APPDATA%\Wallpaper\shellext.log. Messages are plain ASCII on purpose.
static void WriteLog(const char* format, ...)
{
    wchar_t path[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_APPDATA, nullptr, SHGFP_TYPE_CURRENT, path))) return;
    if (!PathAppendW(path, kLogFileName)) return;

    const HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                    nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;

    char message[384] = {};
    va_list arguments;
    va_start(arguments, format);
    StringCchVPrintfA(message, ARRAYSIZE(message), format, arguments);
    va_end(arguments);

    SYSTEMTIME now = {};
    GetLocalTime(&now);

    char line[512] = {};
    StringCchPrintfA(line, ARRAYSIZE(line), "[%04u-%02u-%02u %02u:%02u:%02u] %s\r\n",
                     now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, message);

    DWORD written = 0;
    WriteFile(file, line, static_cast<DWORD>(lstrlenA(line)), &written, nullptr);
    CloseHandle(file);
}

// The background program creates and removes that flag; while it exists a picture is waiting and
// the entry may be used.
static bool IsNextPictureReady()
{
    wchar_t path[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_APPDATA, nullptr, SHGFP_TYPE_CURRENT, path)))
    {
        return true;   // fail open: never block the user because of a lookup problem
    }

    if (!PathAppendW(path, kFlagFileName)) return true;
    return GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES;
}

// Drops the flag so the entry greys out from this very moment. Waiting for the background program
// to notice the command file leaves a window of up to a polling interval in which a second click
// still looks possible - that is how a double switch could be triggered by accident.
static void ClearReadyFlag()
{
    wchar_t path[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_APPDATA, nullptr, SHGFP_TYPE_CURRENT, path))) return;
    if (!PathAppendW(path, kFlagFileName)) return;

    if (DeleteFileW(path))
    {
        WriteLog("Invoke: ready flag cleared, entry greys out now");
    }
    else
    {
        WriteLog("Invoke: ready flag was already absent");
    }
}

static const wchar_t* CommandTitle()
{
    const LANGID language = GetUserDefaultUILanguage();
    const bool simplifiedChinese = ((language & 0x3FF) == 0x04) && (((language >> 10) & 0x3F) == 0x02);
    return simplifiedChinese ? L"切换至下一张壁纸" : L"Next Wallpaper";
}

// Where the executable lives. The registration sits in HKEY_LOCAL_MACHINE (the shell does not use a
// per-user in-process class registration), so that is where it is read back from - reading HKCU
// found nothing and made a click do nothing at all. If the value is missing, the executable that
// sits next to this DLL is used instead.
static void LaunchProgram()
{
    wchar_t executable[MAX_PATH] = {};

    wchar_t clsidText[64] = {};
    if (!StringFromGUID2(CLSID_SimpleWallpaperCommand, clsidText, ARRAYSIZE(clsidText)))
    {
        WriteLog("Invoke: cannot format the CLSID");
        return;
    }

    wchar_t subKey[256] = {};
    if (SUCCEEDED(StringCchPrintfW(subKey, ARRAYSIZE(subKey), L"Software\\Classes\\CLSID\\%s", clsidText)))
    {
        HKEY key = nullptr;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, subKey, 0, KEY_QUERY_VALUE, &key) == ERROR_SUCCESS)
        {
            DWORD size = sizeof(executable);
            RegQueryValueExW(key, L"ExePath", nullptr, nullptr,
                             reinterpret_cast<LPBYTE>(executable), &size);
            RegCloseKey(key);
        }
    }

    if (executable[0] == L'\0')
    {
        if (g_thisModule == nullptr ||
            GetModuleFileNameW(g_thisModule, executable, ARRAYSIZE(executable)) == 0)
        {
            WriteLog("Invoke: cannot locate the program to start");
            return;
        }

        wchar_t* lastSlash = wcsrchr(executable, L'\\');
        if (lastSlash == nullptr)
        {
            WriteLog("Invoke: cannot derive the program folder");
            return;
        }

        lastSlash[1] = L'\0';
        StringCchCatW(executable, ARRAYSIZE(executable), kExecutableName);
    }

    wchar_t commandLine[MAX_PATH + 32] = {};
    if (FAILED(StringCchPrintfW(commandLine, ARRAYSIZE(commandLine), L"\"%s\" --next", executable)))
    {
        WriteLog("Invoke: cannot build the command line");
        return;
    }

    STARTUPINFOW startup = { sizeof(startup) };
    PROCESS_INFORMATION process = {};
    if (CreateProcessW(nullptr, commandLine, nullptr, nullptr, FALSE,
                       CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process))
    {
        WriteLog("Invoke: started the program");
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
    }
    else
    {
        WriteLog("Invoke: CreateProcess failed (error %lu)", GetLastError());
    }
}

template <typename T>
static void DestroyInPlace(T* instance)
{
    if (instance == nullptr) return;
    instance->~T();
    HeapFree(GetProcessHeap(), 0, instance);
}

class SimpleWallpaperCommand final : public IExplorerCommand
{
public:
    // Built in place, so the extension needs no C++ runtime of its own inside explorer.exe.
    static SimpleWallpaperCommand* Create()
    {
        void* memory = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(SimpleWallpaperCommand));
        return memory != nullptr ? new (memory) SimpleWallpaperCommand() : nullptr;
    }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** result) override
    {
        if (result == nullptr) return E_POINTER;
        *result = nullptr;

        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IExplorerCommand))
        {
            *result = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }

        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_references); }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const ULONG remaining = InterlockedDecrement(&_references);
        if (remaining == 0) DestroyInPlace(this);
        return remaining;
    }

    // IExplorerCommand
    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override
    {
        if (name == nullptr) return E_POINTER;
        return SHStrDupW(CommandTitle(), name);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override
    {
        if (icon != nullptr) *icon = nullptr;
        return E_NOTIMPL;   // the program intentionally has no icon
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* toolTip) override
    {
        if (toolTip != nullptr) *toolTip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* name) override
    {
        if (name == nullptr) return E_POINTER;
        *name = GUID_NULL;
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override
    {
        if (state == nullptr) return E_POINTER;

        // ECS_DISABLED greys the entry out: still shown, but not clickable.
        const bool ready = IsNextPictureReady();
        *state = ready ? ECS_ENABLED : ECS_DISABLED;
        WriteLog(ready ? "GetState: enabled" : "GetState: disabled (entry greys out)");
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray*, IBindCtx*) override
    {
        WriteLog("Invoke: menu entry clicked");

        // Grey out first, then hand the work to the program: a second click must already be
        // impossible while the switch is still on its way.
        ClearReadyFlag();
        LaunchProgram();
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        if (flags == nullptr) return E_POINTER;
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** enumerator) override
    {
        if (enumerator != nullptr) *enumerator = nullptr;
        return E_NOTIMPL;
    }

private:
    SimpleWallpaperCommand() : _references(1) {}

    LONG _references;
};

class SimpleWallpaperClassFactory final : public IClassFactory
{
public:
    static SimpleWallpaperClassFactory* Create()
    {
        void* memory = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(SimpleWallpaperClassFactory));
        return memory != nullptr ? new (memory) SimpleWallpaperClassFactory() : nullptr;
    }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** result) override
    {
        if (result == nullptr) return E_POINTER;
        *result = nullptr;

        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory))
        {
            *result = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }

        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_references); }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const ULONG remaining = InterlockedDecrement(&_references);
        if (remaining == 0) DestroyInPlace(this);
        return remaining;
    }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** result) override
    {
        if (outer != nullptr) return CLASS_E_NOAGGREGATION;

        auto* command = SimpleWallpaperCommand::Create();
        if (command == nullptr) return E_OUTOFMEMORY;

        const HRESULT hr = command->QueryInterface(riid, result);
        command->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL) override { return S_OK; }

private:
    SimpleWallpaperClassFactory() : _references(1) {}

    LONG _references;
};

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_thisModule = instance;
    }

    return TRUE;
}

extern "C" __declspec(dllexport) HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void** result)
{
    if (result == nullptr) return E_POINTER;
    *result = nullptr;

    if (!IsEqualCLSID(clsid, CLSID_SimpleWallpaperCommand)) return CLASS_E_CLASSNOTAVAILABLE;

    auto* factory = SimpleWallpaperClassFactory::Create();
    if (factory == nullptr) return E_OUTOFMEMORY;

    const HRESULT hr = factory->QueryInterface(riid, result);
    factory->Release();
    return hr;
}

extern "C" __declspec(dllexport) HRESULT __stdcall DllCanUnloadNow()
{
    return S_FALSE;   // stay loaded: this is a tiny extension, and it avoids unload races
}
