/*
 * SixDofDirect: a Unity native plugin that hands the eye images to WinlatorXR
 * as direct frames, through the wxr_mod.dll helper in WinlatorXR's Wine build.
 *
 * The helper is loaded at run time, so the game still starts where it is
 * missing (not in WinlatorXR, a Wine build without the bridge, or the direct
 * frames tick box off) and keeps using the frame sync pixel there.
 *
 * Everything that touches D3D11 runs on Unity's render thread, from the
 * callback that GetRenderEventFunc returns, issued with
 * GL.IssuePluginEventAndData. The D3D11 device comes from the first eye texture.
 *
 * Licensed like the rest of this project (MIT, or The Unlicense).
 */

#define COBJMACROS
#include <windows.h>
#include <d3d11.h>
#include <stdint.h>

#define EXPORT __declspec(dllexport)

/* Unity calls render event callbacks with UNITY_INTERFACE_API, __stdcall on Windows */
#define UNITY_API __stdcall

#define EVENT_SUBMIT 1
#define EVENT_SHUTDOWN 2

#define STATE_PENDING 0      /* no frame seen yet */
#define STATE_READY 1        /* the helper took the device */
#define STATE_UNAVAILABLE -1 /* no helper, or it said no; stay with the sync pixel */

/* One eye, as the game rendered it. Matches DirectFrames.EyeSubmit in C#. */
struct sixdof_eye {
    void* texture;          /* ID3D11Texture2D*, from RenderTexture.GetNativeTexturePtr */
    float orientation[4];   /* the XrAPI packet's HMD quaternion x, y, z, w, unchanged */
    float position[3];      /* the packet's HMD position, unchanged */
    float ipd;              /* meters; 0 for a single middle view */
    float fov_x, fov_y;     /* degrees, as the packet gives them */
    int32_t eye;            /* 0 left, 1 right */
    int32_t reserved;
};

/* Matches DirectFrames.FrameSubmit in C#. */
struct sixdof_frame {
    int32_t count;
    int32_t reserved;
    struct sixdof_eye eyes[2];
};

/* What wxr_mod.dll exports (see wxr_mod.h in WinlatorXR's tools/wxr_mod) */
struct wxr_mod_eye {
    float orientation[4];
    float position[3];
    float fov[4];
};

typedef int(__cdecl* init_fn)(ID3D11Device*);
typedef int(__cdecl* submit_eye_fn)(int, ID3D11Texture2D*, const int32_t*, const struct wxr_mod_eye*);
typedef int(__cdecl* present_fn)(void);
typedef void(__cdecl* shutdown_fn)(void);
typedef void(__cdecl* eye_from_hmd_fn)(const float*, const float*, float, float, float, int, struct wxr_mod_eye*);

static HMODULE g_module;
static init_fn g_init;
static submit_eye_fn g_submit_eye;
static present_fn g_present;
static shutdown_fn g_shutdown;
static eye_from_hmd_fn g_eye_from_hmd;

static volatile LONG g_state = STATE_PENDING;
static volatile LONG g_showing;
static volatile LONG g_failed_submits;

static int load_helper(void)
{
    if (!(g_module = LoadLibraryW(L"wxr_mod.dll"))) return 0;
    g_init = (init_fn)(void*)GetProcAddress(g_module, "WxrModInit");
    g_submit_eye = (submit_eye_fn)(void*)GetProcAddress(g_module, "WxrModSubmitEye");
    g_present = (present_fn)(void*)GetProcAddress(g_module, "WxrModPresent");
    g_shutdown = (shutdown_fn)(void*)GetProcAddress(g_module, "WxrModShutdown");
    g_eye_from_hmd = (eye_from_hmd_fn)(void*)GetProcAddress(g_module, "WxrModEyeFromHmd");
    if (g_init && g_submit_eye && g_present && g_shutdown && g_eye_from_hmd) return 1;
    FreeLibrary(g_module);
    g_module = NULL;
    return 0;
}

static void init(ID3D11Texture2D* texture)
{
    ID3D11Device* device = NULL;
    int ok = 0;

    if (texture && load_helper()) {
        ID3D11Texture2D_GetDevice(texture, &device);
        ok = device && g_init(device);  /* the helper keeps its own reference */
        if (device) ID3D11Device_Release(device);
    }
    InterlockedExchange(&g_state, ok ? STATE_READY : STATE_UNAVAILABLE);
}

static void submit(const struct sixdof_frame* frame)
{
    struct wxr_mod_eye pose;
    int i, count = frame->count < 2 ? frame->count : 2;

    if (g_state == STATE_PENDING && count > 0) init((ID3D11Texture2D*)frame->eyes[0].texture);
    if (g_state != STATE_READY) return;

    for (i = 0; i < count; i++) {
        const struct sixdof_eye* e = &frame->eyes[i];
        if (!e->texture) continue;
        g_eye_from_hmd(e->orientation, e->position, e->ipd, e->fov_x, e->fov_y, e->eye, &pose);
        if (!g_submit_eye(e->eye, (ID3D11Texture2D*)e->texture, NULL, &pose))
            InterlockedIncrement(&g_failed_submits);
    }
    InterlockedExchange(&g_showing, g_present());
}

static void UNITY_API on_render_event(int event, void* data)
{
    if (event == EVENT_SUBMIT && data) {
        submit((const struct sixdof_frame*)data);
    } else if (event == EVENT_SHUTDOWN && g_state == STATE_READY) {
        g_shutdown();
        InterlockedExchange(&g_showing, 0);
        InterlockedExchange(&g_state, STATE_UNAVAILABLE);
    }
}

EXPORT void* __cdecl SixDofDirect_GetRenderEventFunc(void)
{
    return (void*)on_render_event;
}

/* STATE_PENDING, STATE_READY or STATE_UNAVAILABLE */
EXPORT int __cdecl SixDofDirect_GetState(void)
{
    return (int)g_state;
}

/* 1 while WinlatorXR shows the submitted eyes instead of the game window */
EXPORT int __cdecl SixDofDirect_IsShowing(void)
{
    return (int)g_showing;
}

EXPORT int __cdecl SixDofDirect_GetFailedSubmits(void)
{
    return (int)g_failed_submits;
}
