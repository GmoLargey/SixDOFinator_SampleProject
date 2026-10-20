using System;
using System.Runtime.InteropServices;
using UnityEngine;

// Hands the eye images to WinlatorXR as direct frames instead of drawing them in the
// game window, through the SixDofDirect native plugin (Assets/Plugins) and the wxr_mod.dll
// helper in WinlatorXR's Wine build. Each eye goes with the HMD pose it was rendered
// from, so WinlatorXR can place it correctly even when a frame arrives late.
//
// While direct frames are on, the eye cameras render into textures at headset
// resolution. The game window shows a copy of them until WinlatorXR takes the
// frames, so the frame sync pixel path still works as a fallback. Where the helper
// is missing (outside WinlatorXR, Wine builds without it, or the shortcut's direct
// frames tick box off) nothing changes and the game works as before.
public class DirectFrames : MonoBehaviour
{
    [SerializeField]
    private bool m_EnableDirectFrames = true;

    [Tooltip("Height of each eye image in pixels; the width follows from the headset's field of view")]
    [SerializeField]
    private int m_EyeHeight = 1280;

    [Tooltip("Copy the eye images into the game window while WinlatorXR is not yet showing them")]
    [SerializeField]
    private bool m_MirrorToWindow = true;

    // Must match struct sixdof_eye and struct sixdof_frame in NativePlugin/SixDofDirect/sixdof_direct.c
    [StructLayout(LayoutKind.Sequential)]
    private struct EyeSubmit
    {
        public IntPtr texture;
        public float qx, qy, qz, qw;
        public float px, py, pz;
        public float ipd, fovX, fovY;
        public int eye;
        public int reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameSubmit
    {
        public int count;
        public int reserved;
        public EyeSubmit eye0;
        public EyeSubmit eye1;
    }

    [DllImport("SixDofDirect", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SixDofDirect_GetRenderEventFunc();
    [DllImport("SixDofDirect", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SixDofDirect_GetState();
    [DllImport("SixDofDirect", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SixDofDirect_IsShowing();
    [DllImport("SixDofDirect", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SixDofDirect_GetFailedSubmits();

    private const int EVENT_SUBMIT = 1;
    private const int EVENT_SHUTDOWN = 2;

    private const int STATE_PENDING = 0;
    private const int STATE_READY = 1;
    private const int STATE_UNAVAILABLE = -1;

    // The render thread reads a frame after the main thread has moved on, so each
    // frame gets its own block of native memory. Shared by every player in every
    // scene and kept for the life of the process.
    private const int RING_SIZE = 4;
    private static IntPtr[] s_Ring;
    private static int s_RingIndex;
    private static IntPtr s_RenderEvent = IntPtr.Zero;
    private static bool s_PluginChecked;
    private static bool s_ShutdownSent;

    private int state = STATE_UNAVAILABLE;
    private bool loggedState;

    private readonly RenderTexture[] eyeTextures = new RenderTexture[2];
    private readonly IntPtr[] eyeTexturePtrs = new IntPtr[2];

    private Camera redirectedFlat;
    private Camera redirectedLeft;
    private Camera redirectedRight;
    private Rect savedFlatRect;
    private Rect savedLeftRect;
    private Rect savedRightRect;
    private bool redirectedStereo;

    private FrameSubmit pending;

    void Awake()
    {
        if (!s_PluginChecked)
        {
            s_PluginChecked = true;

            try
            {
                s_RenderEvent = SixDofDirect_GetRenderEventFunc();

                s_Ring = new IntPtr[RING_SIZE];
                for (int i = 0; i < RING_SIZE; i++)
                {
                    s_Ring[i] = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FrameSubmit)));
                }
            }
            catch (Exception e)
            {
                s_RenderEvent = IntPtr.Zero;
                Debug.Log("[SixDOFinator] Direct frames plugin not loaded: " + e.Message);
            }
        }

        RefreshState();
    }

    // True while the eyes should go to WinlatorXR: the plugin is waiting for its first frame, or the helper took the device
    public bool Active
    {
        get { return m_EnableDirectFrames && state != STATE_UNAVAILABLE; }
    }

    // True while WinlatorXR shows the submitted eyes rather than the game window
    public bool Showing
    {
        get { return state == STATE_READY && SixDofDirect_IsShowing() != 0; }
    }

    private void RefreshState()
    {
        if (s_RenderEvent == IntPtr.Zero || s_ShutdownSent)
        {
            state = STATE_UNAVAILABLE;
            return;
        }

        state = SixDofDirect_GetState();

        if (!loggedState && state != STATE_PENDING)
        {
            loggedState = true;
            Debug.Log(state == STATE_READY
                ? "[SixDOFinator] Direct frames on"
                : "[SixDOFinator] Direct frames not available, using the frame sync pixel");
        }
    }

    // Called each frame by ReadPosData after it has set the camera field of view.
    // Points the cameras in use at the eye textures, or back at the window when direct frames are off.
    public void PrepareCameras(Camera flatCam, Camera leftCam, Camera rightCam, bool stereo, float fovH, float fovV)
    {
        RefreshState();

        if (!Active || fovH <= 0f || fovV <= 0f || (stereo ? (leftCam == null || rightCam == null) : flatCam == null))
        {
            RestoreCameras();
            return;
        }

        // Unity derives the horizontal field of view from the aspect, through the tangents,
        // so this is the aspect that gives WinlatorXR's horizontal field of view exactly
        float aspect = Mathf.Tan(fovH * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(fovV * 0.5f * Mathf.Deg2Rad);
        int height = Mathf.Max(64, m_EyeHeight);
        int width = Mathf.Max(64, Mathf.RoundToInt(height * aspect));

        if (redirectedStereo != stereo) RestoreCameras();

        if (stereo)
        {
            EnsureEyeTexture(0, width, height);
            EnsureEyeTexture(1, width, height);
            Redirect(ref redirectedLeft, ref savedLeftRect, leftCam, eyeTextures[0], fovV, aspect);
            Redirect(ref redirectedRight, ref savedRightRect, rightCam, eyeTextures[1], fovV, aspect);
        }
        else
        {
            // AER and mono both draw with the one camera
            EnsureEyeTexture(0, width, height);
            Redirect(ref redirectedFlat, ref savedFlatRect, flatCam, eyeTextures[0], fovV, aspect);
        }

        redirectedStereo = stereo;
    }

    private void EnsureEyeTexture(int index, int width, int height)
    {
        RenderTexture rt = eyeTextures[index];

        if (rt != null && rt.width == width && rt.height == height && rt.IsCreated()) return;

        if (rt != null)
        {
            if (redirectedLeft != null && redirectedLeft.targetTexture == rt) redirectedLeft.targetTexture = null;
            if (redirectedRight != null && redirectedRight.targetTexture == rt) redirectedRight.targetTexture = null;
            if (redirectedFlat != null && redirectedFlat.targetTexture == rt) redirectedFlat.targetTexture = null;
            rt.Release();
            Destroy(rt);
        }

        // Single sample RGBA8: the format the helper copies from
        rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        rt.name = index == 0 ? "DirectFramesEyeL" : "DirectFramesEyeR";
        rt.antiAliasing = 1;
        rt.Create();

        eyeTextures[index] = rt;
        eyeTexturePtrs[index] = rt.GetNativeTexturePtr();
    }

    private static void Redirect(ref Camera redirected, ref Rect savedRect, Camera cam, RenderTexture target, float fovV, float aspect)
    {
        if (redirected != cam)
        {
            redirected = cam;
            savedRect = cam.rect;
        }

        cam.targetTexture = target;
        cam.rect = new Rect(0f, 0f, 1f, 1f);
        cam.fieldOfView = fovV;
        cam.aspect = aspect;
    }

    private void RestoreCameras()
    {
        Restore(ref redirectedFlat, savedFlatRect);
        Restore(ref redirectedLeft, savedLeftRect);
        Restore(ref redirectedRight, savedRightRect);
    }

    private static void Restore(ref Camera redirected, Rect savedRect)
    {
        if (redirected == null) return;

        redirected.targetTexture = null;
        redirected.rect = savedRect;
        redirected.ResetAspect();
        redirected = null;
    }

    // Called from ReadPosData.PreRenderRead with the XrAPI packet values the frame is rendered from, passed unchanged.
    // stereo: both eyes. aer: only the eye drawn this frame. Otherwise one middle view.
    public void SetFramePose(bool stereo, bool aer, bool aerLeftEye,
                             float qx, float qy, float qz, float qw,
                             float px, float py, float pz,
                             float ipd, float fovH, float fovV)
    {
        pending.count = 0;

        if (!Active || (redirectedFlat == null && redirectedLeft == null)) return;

        EyeSubmit eye = new EyeSubmit();
        eye.qx = qx; eye.qy = qy; eye.qz = qz; eye.qw = qw;
        eye.px = px; eye.py = py; eye.pz = pz;
        eye.ipd = ipd;
        eye.fovX = fovH;
        eye.fovY = fovV;

        if (stereo && redirectedStereo)
        {
            eye.texture = eyeTexturePtrs[0];
            eye.eye = 0;
            pending.eye0 = eye;

            eye.texture = eyeTexturePtrs[1];
            eye.eye = 1;
            pending.eye1 = eye;

            pending.count = 2;
        }
        else if (!stereo && !redirectedStereo)
        {
            eye.texture = eyeTexturePtrs[0];

            if (aer)
            {
                eye.eye = aerLeftEye ? 0 : 1;
            }
            else
            {
                // A single middle view, which WinlatorXR shows to both eyes
                eye.eye = 0;
                eye.ipd = 0f;
            }

            pending.eye0 = eye;
            pending.count = 1;
        }
    }

    // Called from ReadPosData.PostRenderRelease once the last eye camera has rendered
    public void SubmitFrame()
    {
        if (pending.count == 0 || s_RenderEvent == IntPtr.Zero || s_ShutdownSent) return;

        IntPtr slot = s_Ring[s_RingIndex];
        s_RingIndex = (s_RingIndex + 1) % RING_SIZE;

        Marshal.StructureToPtr(pending, slot, false);
        GL.IssuePluginEventAndData(s_RenderEvent, EVENT_SUBMIT, slot);

        pending.count = 0;
    }

    public int FailedSubmits()
    {
        return s_RenderEvent == IntPtr.Zero ? 0 : SixDofDirect_GetFailedSubmits();
    }

    void OnGUI()
    {
        if (!m_MirrorToWindow || Event.current.type != EventType.Repaint) return;
        if (redirectedFlat == null && redirectedLeft == null) return;
        if (Showing) return;

        if (redirectedStereo)
        {
            if (eyeTextures[0] != null) GUI.DrawTexture(new Rect(0, 0, Screen.width * 0.5f, Screen.height), eyeTextures[0], ScaleMode.StretchToFill, false);
            if (eyeTextures[1] != null) GUI.DrawTexture(new Rect(Screen.width * 0.5f, 0, Screen.width * 0.5f, Screen.height), eyeTextures[1], ScaleMode.StretchToFill, false);
        }
        else if (eyeTextures[0] != null)
        {
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), eyeTextures[0], ScaleMode.StretchToFill, false);
        }
    }

    void OnDestroy()
    {
        // A scene change destroys the player; the next scene's player carries on with the same plugin state
        RestoreCameras();

        for (int i = 0; i < eyeTextures.Length; i++)
        {
            if (eyeTextures[i] != null)
            {
                eyeTextures[i].Release();
                Destroy(eyeTextures[i]);
                eyeTextures[i] = null;
                eyeTexturePtrs[i] = IntPtr.Zero;
            }
        }
    }

    void OnApplicationQuit()
    {
        if (s_RenderEvent == IntPtr.Zero || s_ShutdownSent) return;

        s_ShutdownSent = true;
        GL.IssuePluginEvent(s_RenderEvent, EVENT_SHUTDOWN);
    }
}
