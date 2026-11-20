#include <windows.h>
#include <tlhelp32.h>
#include <string.h>

// 游戏自己的窗口句柄
static HWND g_hMyWnd = NULL;

// ========== 替身函数：永远告诉游戏"你就是前台" ==========
static HWND WINAPI HookGetForegroundWindow() { return g_hMyWnd; }
static HWND WINAPI HookGetActiveWindow() { return g_hMyWnd; }
static HWND WINAPI HookGetFocus() { return g_hMyWnd; }

struct HookTarget { const char* name; void* hook; };

static HookTarget g_targets[] = {
    { "GetForegroundWindow", (void*)&HookGetForegroundWindow },
    { "GetActiveWindow",     (void*)&HookGetActiveWindow },
    { "GetFocus",            (void*)&HookGetFocus },
};
static const int g_targetCount = sizeof(g_targets) / sizeof(g_targets[0]);

// 枚举属于本进程的可见顶层窗口
static BOOL CALLBACK EnumMyWindows(HWND hwnd, LPARAM lParam)
{
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid == GetCurrentProcessId() && IsWindowVisible(hwnd))
    {
        g_hMyWnd = hwnd;
        return FALSE; // 找到就停
    }
    return TRUE;
}

// ========== 把一个模块导入表(IAT)里的目标函数指针换掉 ==========
static void PatchModuleIAT(HMODULE hMod)
{
    BYTE* base = (BYTE*)hMod;

    IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;

    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return;

    IMAGE_DATA_DIRECTORY* importDir =
        &nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (importDir->VirtualAddress == 0) return;

    IMAGE_IMPORT_DESCRIPTOR* imp =
        (IMAGE_IMPORT_DESCRIPTOR*)(base + importDir->VirtualAddress);

    for (; imp->Name != 0; ++imp)
    {
        const char* dllName = (const char*)(base + imp->Name);
        if (_stricmp(dllName, "user32.dll") != 0) continue;

        IMAGE_THUNK_DATA* origThunk = (IMAGE_THUNK_DATA*)(base + imp->OriginalFirstThunk);
        IMAGE_THUNK_DATA* iatThunk = (IMAGE_THUNK_DATA*)(base + imp->FirstThunk);

        for (; origThunk->u1.AddressOfData != 0; ++origThunk, ++iatThunk)
        {
            // 按序号导入的跳过
            if (IMAGE_SNAP_BY_ORDINAL(origThunk->u1.Ordinal)) continue;

            IMAGE_IMPORT_BY_NAME* byName =
                (IMAGE_IMPORT_BY_NAME*)(base + origThunk->u1.AddressOfData);

            for (int i = 0; i < g_targetCount; ++i)
            {
                if (strcmp(byName->Name, g_targets[i].name) == 0)
                {
                    DWORD oldProtect;
                    if (VirtualProtect(&iatThunk->u1.Function, sizeof(void*), PAGE_READWRITE, &oldProtect))
                    {
                        iatThunk->u1.Function = (ULONG_PTR)g_targets[i].hook;
                        VirtualProtect(&iatThunk->u1.Function, sizeof(void*), oldProtect, &oldProtect);
                    }
                }
            }
        }
    }
}

// ========== 遍历本进程所有模块，逐个改导入表 ==========
static void PatchAllModules()
{
    HANDLE snap = CreateToolhelp32Snapshot(
        TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
    if (snap == INVALID_HANDLE_VALUE) return;

    MODULEENTRY32 me;
    me.dwSize = sizeof(me);

    if (Module32First(snap, &me))
    {
        do {
            PatchModuleIAT((HMODULE)me.modBaseAddr);
        } while (Module32Next(snap, &me));
    }

    CloseHandle(snap);
}

// ========== 注入后执行的线程 ==========
static DWORD WINAPI MainThread(LPVOID param)
{
    // 先等游戏窗口出现（最多 10 秒）
    for (int i = 0; i < 50 && g_hMyWnd == NULL; ++i)
    {
        EnumWindows(EnumMyWindows, 0);
        if (g_hMyWnd == NULL) Sleep(200);
    }

    if (g_hMyWnd == NULL) return 1; // 没找到窗口就不钩

    PatchAllModules();
    return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(hModule);
        CreateThread(NULL, 0, MainThread, NULL, 0, NULL);
    }
    return TRUE;
}