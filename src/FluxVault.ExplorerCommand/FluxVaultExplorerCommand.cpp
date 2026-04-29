#include <windows.h>
#include <shobjidl.h>
#include <shellapi.h>
#include <strsafe.h>

#include <array>
#include <new>
#include <string>

#include <initguid.h>

#ifndef RETURN_IF_FAILED
#define RETURN_IF_FAILED(expression) \
    do \
    { \
        const HRESULT returnIfFailedHr = (expression); \
        if (FAILED(returnIfFailedHr)) \
        { \
            return returnIfFailedHr; \
        } \
    } while (false)
#endif

DEFINE_GUID(CLSID_FluxVaultAddCommand, 0xb43c1563, 0x662c, 0x4fa2, 0xa2, 0x69, 0x15, 0x29, 0x5a, 0x9b, 0x05, 0xe7);
DEFINE_GUID(CLSID_FluxVaultShowVersionsCommand, 0x5af731bc, 0x67df, 0x4ec5, 0xbc, 0x41, 0x91, 0xfc, 0x89, 0xd6, 0xa4, 0xb9);
DEFINE_GUID(CLSID_FluxVaultRemoveCommand, 0xb530a520, 0x66ef, 0x4a82, 0xb6, 0xc4, 0x5a, 0xa8, 0xf1, 0x5d, 0x0f, 0x2e);

namespace
{
    HINSTANCE g_instance = nullptr;
    long g_objectCount = 0;

    struct VerbDefinition
    {
        const GUID* Clsid;
        PCWSTR Title;
        PCWSTR ToolTip;
        PCWSTR Argument;
    };

    constexpr std::array<VerbDefinition, 3> Verbs =
    {
        VerbDefinition{ &CLSID_FluxVaultAddCommand, L"Add to FluxVault", L"Add this item to FluxVault protection.", L"--add-path" },
        VerbDefinition{ &CLSID_FluxVaultShowVersionsCommand, L"Show FluxVault versions", L"Open FluxVault versions for this item.", L"--show-versions" },
        VerbDefinition{ &CLSID_FluxVaultRemoveCommand, L"Remove from FluxVault", L"Remove this item from FluxVault protection.", L"--remove-path" }
    };

    HRESULT CopyCoTaskMemString(PCWSTR value, PWSTR* result)
    {
        if (result == nullptr)
        {
            return E_POINTER;
        }

        *result = nullptr;
        const size_t length = wcslen(value) + 1;
        auto buffer = static_cast<PWSTR>(CoTaskMemAlloc(length * sizeof(wchar_t)));
        if (buffer == nullptr)
        {
            return E_OUTOFMEMORY;
        }

        HRESULT hr = StringCchCopyW(buffer, length, value);
        if (FAILED(hr))
        {
            CoTaskMemFree(buffer);
            return hr;
        }

        *result = buffer;
        return S_OK;
    }

    HRESULT GetFirstItemPath(IShellItemArray* items, std::wstring& path)
    {
        if (items == nullptr)
        {
            return E_INVALIDARG;
        }

        DWORD count = 0;
        RETURN_IF_FAILED(items->GetCount(&count));
        if (count == 0)
        {
            return E_INVALIDARG;
        }

        IShellItem* item = nullptr;
        RETURN_IF_FAILED(items->GetItemAt(0, &item));

        PWSTR displayName = nullptr;
        const HRESULT hr = item->GetDisplayName(SIGDN_FILESYSPATH, &displayName);
        item->Release();
        if (FAILED(hr))
        {
            return hr;
        }

        path = displayName;
        CoTaskMemFree(displayName);
        return S_OK;
    }

    std::wstring GetFluxVaultAppPath()
    {
        wchar_t modulePath[MAX_PATH]{};
        GetModuleFileNameW(g_instance, modulePath, ARRAYSIZE(modulePath));
        std::wstring directory = modulePath;
        const auto slash = directory.find_last_of(L"\\/");
        if (slash != std::wstring::npos)
        {
            directory.resize(slash);
        }

        const auto folder = directory.find_last_of(L"\\/");
        if (folder != std::wstring::npos
            && _wcsicmp(directory.c_str() + folder + 1, L"shell-extension") == 0)
        {
            directory.resize(folder);
            return directory + L"\\app\\FluxVault.App.exe";
        }

        return directory + L"\\FluxVault.App.exe";
    }

    HRESULT LaunchFluxVault(PCWSTR argument, const std::wstring& path)
    {
        std::wstring parameters(argument);
        parameters += L" \"";
        parameters += path;
        parameters += L"\"";

        const auto result = reinterpret_cast<INT_PTR>(ShellExecuteW(
            nullptr,
            L"open",
            GetFluxVaultAppPath().c_str(),
            parameters.c_str(),
            nullptr,
            SW_SHOWNORMAL));

        return result > 32 ? S_OK : HRESULT_FROM_WIN32(static_cast<DWORD>(result));
    }

    template <typename T>
    HRESULT QueryInterfaceImpl(T* instance, REFIID riid, void** result)
    {
        if (result == nullptr)
        {
            return E_POINTER;
        }

        *result = nullptr;
        if (riid == IID_IUnknown || riid == __uuidof(IExplorerCommand))
        {
            *result = static_cast<IExplorerCommand*>(instance);
        }
        else if (riid == __uuidof(IExplorerCommandState))
        {
            *result = static_cast<IExplorerCommandState*>(instance);
        }
        else
        {
            return E_NOINTERFACE;
        }

        instance->AddRef();
        return S_OK;
    }

    class FluxVaultExplorerCommand final : public IExplorerCommand, public IExplorerCommandState
    {
    public:
        explicit FluxVaultExplorerCommand(const VerbDefinition& verb) : verb_(verb)
        {
            InterlockedIncrement(&g_objectCount);
        }

        ~FluxVaultExplorerCommand() override
        {
            InterlockedDecrement(&g_objectCount);
        }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** result) override
        {
            return QueryInterfaceImpl(this, riid, result);
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            return InterlockedIncrement(&refCount_);
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const auto count = InterlockedDecrement(&refCount_);
            if (count == 0)
            {
                delete this;
            }

            return count;
        }

        IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* name) override
        {
            return CopyCoTaskMemString(verb_.Title, name);
        }

        IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
        {
            return CopyCoTaskMemString(GetFluxVaultAppPath().c_str(), icon);
        }

        IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tooltip) override
        {
            return CopyCoTaskMemString(verb_.ToolTip, tooltip);
        }

        IFACEMETHODIMP GetCanonicalName(GUID* guidCommandName) override
        {
            if (guidCommandName == nullptr)
            {
                return E_POINTER;
            }

            *guidCommandName = *verb_.Clsid;
            return S_OK;
        }

        IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override
        {
            if (state == nullptr)
            {
                return E_POINTER;
            }

            DWORD count = 0;
            if (items == nullptr || FAILED(items->GetCount(&count)) || count != 1)
            {
                *state = ECS_DISABLED;
                return S_OK;
            }

            *state = ECS_ENABLED;
            return S_OK;
        }

        IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
        {
            std::wstring path;
            RETURN_IF_FAILED(GetFirstItemPath(items, path));
            return LaunchFluxVault(verb_.Argument, path);
        }

        IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
        {
            if (flags == nullptr)
            {
                return E_POINTER;
            }

            *flags = ECF_DEFAULT;
            return S_OK;
        }

        IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** enumCommands) override
        {
            if (enumCommands == nullptr)
            {
                return E_POINTER;
            }

            *enumCommands = nullptr;
            return E_NOTIMPL;
        }

    private:
        long refCount_ = 1;
        VerbDefinition verb_;
    };

    class FluxVaultExplorerCommandFactory final : public IClassFactory
    {
    public:
        explicit FluxVaultExplorerCommandFactory(const VerbDefinition& verb) : verb_(verb)
        {
            InterlockedIncrement(&g_objectCount);
        }

        ~FluxVaultExplorerCommandFactory() override
        {
            InterlockedDecrement(&g_objectCount);
        }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** result) override
        {
            if (result == nullptr)
            {
                return E_POINTER;
            }

            *result = nullptr;
            if (riid == IID_IUnknown || riid == IID_IClassFactory)
            {
                *result = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }

            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            return InterlockedIncrement(&refCount_);
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const auto count = InterlockedDecrement(&refCount_);
            if (count == 0)
            {
                delete this;
            }

            return count;
        }

        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** result) override
        {
            if (outer != nullptr)
            {
                return CLASS_E_NOAGGREGATION;
            }

            auto command = new (std::nothrow) FluxVaultExplorerCommand(verb_);
            if (command == nullptr)
            {
                return E_OUTOFMEMORY;
            }

            const HRESULT hr = command->QueryInterface(riid, result);
            command->Release();
            return hr;
        }

        IFACEMETHODIMP LockServer(BOOL lock) override
        {
            if (lock)
            {
                InterlockedIncrement(&g_objectCount);
            }
            else
            {
                InterlockedDecrement(&g_objectCount);
            }

            return S_OK;
        }

    private:
        long refCount_ = 1;
        VerbDefinition verb_;
    };
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_instance = module;
        DisableThreadLibraryCalls(module);
    }

    return TRUE;
}

STDAPI DllCanUnloadNow()
{
    return g_objectCount == 0 ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** result)
{
    for (const auto& verb : Verbs)
    {
        if (IsEqualCLSID(clsid, *verb.Clsid))
        {
            auto factory = new (std::nothrow) FluxVaultExplorerCommandFactory(verb);
            if (factory == nullptr)
            {
                return E_OUTOFMEMORY;
            }

            const HRESULT hr = factory->QueryInterface(riid, result);
            factory->Release();
            return hr;
        }
    }

    return CLASS_E_CLASSNOTAVAILABLE;
}
