# potential OpenXR underswing fix

a smol but hopeful mod that hopefully fixes the underswing issue for bs versions after 1.29.1

it makes a little runtime patch to `UnityOpenXR.dll` in memory when the game starts, nothing on disk is altered

i haven't reproduced the issue in-game because it's cold in australia rn and im lazy but im like 99% sure by downloading & re'ing every historical resource i could find and interrogating [meivyn](https://github.com/Meivyn) / [pulselane](https://github.com/PulseLane) a bit on discord i've narrowed it down a fair bit

## the patch

`xrWaitFrame` gives Unity `T` (`predictedDisplayTime`) and `P` (`predictedDisplayPeriod`). Unity saves `T` for before render input and `T + P` for dynamic input, which is what bs uses for saber motion

`T + P` is valid for a pipelined next frame. The problem is bs uses this sample as current saber input

The unity decomp pretty much boils down to:

```c
context->beforeRenderTime = T;
context->dynamicTime = P == XR_INFINITE_DURATION ? T : T + P;
```

Both steamvr and oculus use that absolute `xrLocateSpace` time for controller prediction

the native helper stores:

```asm
mov [context + 0x28], rax  ; T + P
```

the patch stores:

```asm
mov [context + 0x28], rdx  ; T
```

The same pattern sig works on Unity OpenXR 1.9.1 and 1.14.3, if it doesnt match exactly once nothing is patched

## SteamVR

OpenVR's `WaitGetPoses` returns one render / game pose snapshot, the game pose is already predicted an extra frame

so i cracked open SteamVR 1.15.10 and 1.15.11 side by side:

```c
/* 1.15.10 */
poseId = renderPoseId + 1 + extraIntervals;

/* 1.15.11 */
poseId = renderPoseId;
P = (extraIntervals + 1) * displayPeriod;
```

1.15.10 asks for the OpenVR game pose. 1.15.11 asks for the earlier render pose then reports the missing time as `P`, Unity adds it back later

SteamVR 2.10.2 picks `P` like this:

```c
P = waitThread == beginThread
    ? actualDisplayTime[1] - actualDisplayTime[0]
    : (throttle + 1) * displayPeriod;
```

The first path is the 2.10.2 "fix" that was implemented awhile back. Unity OpenXR calls `xrWaitFrame` then `xrBeginFrame` in the same native function so this is what bs uses

I also wouldn't say that 1.15.12 is a clean control either, slinkstr later tested it against 2.12.2 on bs 1.40.5 and [took the claim back](https://github.com/Meivyn/BeatSaberBugs/issues/8#issuecomment-3029307105)

## Oculus

not as easy to do historical analysis on oculus (if u old have binaries dm me)
but i checked out the current `LibOVRRTImpl64_1.dll` and its `xrWaitFrame` path is:

```c
T = ovr_GetPredictedDisplayTime(session, frameIndex);
P = 1000000000 / refreshRate;
```

Then `xrLocateSpace` does:

```c
seconds = time / 1000000000.0;
tracking = ovr_GetTrackingState(session, seconds, false);
```

ovr then feeds the supplied time into its pose predictor:

```c
prediction = clamp(time - sampleTime, 0, 0.25);
```

one display period is well below that clamp and unitys `T + P` asks for controller tracking one refresh interval after the frame oculus already predicted

this doesnt 100% confirm that oculus has the same issue but at the very least dropping `P` will definitely change controller prediction there too

## references

- [the original underswing investigation](https://github.com/Meivyn/BeatSaberBugs/issues/8)
- [SteamVR 1.15.10 notes](https://steamdb.info/patchnotes/5839127/)
- [SteamVR 1.15.11 notes](https://steamdb.info/patchnotes/5856452/)
- [SteamVR 2.10.2 notes](https://steamdb.info/patchnotes/17946446/)
- [Valve talking about moving cadence into `xrWaitFrame`](https://steamcommunity.com/app/250820/discussions/8/3001046778348674981/)
- [OpenXR 1.1 spec](https://registry.khronos.org/OpenXR/specs/1.1/html/xrspec.html)
