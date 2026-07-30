# OpenXR underswing fix

a client side mod which fixes controller pose jitter / underswing in Beat Saber

it patches `UnityOpenXR.dll` in memory when the game starts, nothing on disk is altered

## the bug

`xrWaitFrame` gives Unity `T` (`predictedDisplayTime`) and `P` (`predictedDisplayPeriod`)

Unity uses `T` for before render input and `T + P` for dynamic input, bs reads dynamic controller state during its normal update

`T` is already predicted and `P` moves it another frame ahead. runtimes can change `P` with frame timing, if it shrinks faster than `T` advances then the requested pose time goes backwards for a frame

## the patch

unity stores:

```c
context->beforeRenderTime = T;
context->dynamicTime = P == XR_INFINITE_DURATION ? T : T + P;
```

the patch makes dynamic input use:

```c
if (P == XR_INFINITE_DURATION)
    dynamicTime = T;
else if (runtime == SteamVR)
    dynamicTime = T + fixedDisplayPeriod;
else
    dynamicTime = T;
```

SteamVR keeps one extra frame of prediction with a fixed period, matching its normal OpenVR game pose without using the changing `P`

the fixed period follows the headset refresh rate and updates if it changes during a session

other runtimes use `T`, matching the old oculus provider's `Step.Render` pose

if the pattern sig doesnt match exactly once it bails

## SteamVR

OpenVR's `WaitGetPoses` returns render and game pose snapshots, bs used the game pose before OpenXR

the normal game pose is one display period ahead of the render pose

SteamVR 1.15.10 and 1.15.11 calculate that pose in different places:

```c
/* 1.15.10 */
poseId = renderPoseId + 1 + extraIntervals;

/* 1.15.11 */
poseId = renderPoseId;
P = (extraIntervals + 1) * displayPeriod;
```

1.15.10 asks for the later pose itself. 1.15.11 asks for the render pose and reports the missing time as `P`, Unity adds it back for dynamic input

SteamVR 2.10.2 calculates `P` like this:

```c
P = waitThread == beginThread
    ? actualDisplayTime[1] - actualDisplayTime[0]
    : (throttle + 1) * displayPeriod;
```

Unity OpenXR calls `xrWaitFrame` and `xrBeginFrame` from the same native function, so bs gets the first path

that value can shrink with frame timing while `xrLocateSpace` still accepts the resulting timestamp. the SteamVR patch keeps the same extra frame with a fixed display period instead

1.15.12 isnt a clean control either, slinkstr later compared it with 2.12.2 on bs 1.40.5 and [took the claim back](https://github.com/Meivyn/BeatSaberBugs/issues/8#issuecomment-3029307105)

## oculus

~~not as easy to do historical analysis on oculus (if u old have binaries dm me)~~

thanks to [whatdahopper](https://github.com/whatdahopper) sending me this i could verify the old oculus path

OpenXR became official in v19, so i cracked open v18 and v20

`xrWaitFrame` has the same CFG in both:

```c
T = ovr_GetPredictedDisplayTime(session, frameIndex);
P = 1000000000 / refreshRate;
```

the oculus provider in 1.29.1 does:

```c
ovrp_Update2(-1, frameIndex, 0);

T = ovr_GetPredictedDisplayTime(session, frameIndex);
renderPose = ovr_GetTrackingState(session, T, true);
```

bs reads that cached `Step.Render` pose

the OpenXR path in both runtimes passes its requested time straight through:

```c
seconds = time / 1000000000.0;
tracking = ovr_GetTrackingState(session, seconds, false);
```

the predictor is the same in v18, v20, v65 and current:

```c
prediction = clamp(time - sampleTime, 0, 0.25);
```

the extra period comes from Unity's dynamic input caller, so the non SteamVR patch uses `T`

## references

- [the original underswing investigation](https://github.com/Meivyn/BeatSaberBugs/issues/8)
- [oculus gestalt archive](https://github.com/BnuuySolutions/Oculus-Gestalt-Collection)
- [oculus making OpenXR official in v19](https://communityforums.atmeta.com/t5/OpenXR-Development/OpenXR-News-and-Feedback-Thread/td-p/765004/page/2)
- [SteamVR 1.15.10 notes](https://steamdb.info/patchnotes/5839127/)
- [SteamVR 1.15.11 notes](https://steamdb.info/patchnotes/5856452/)
- [SteamVR 2.10.2 notes](https://steamdb.info/patchnotes/17946446/)
- [Valve talking about moving cadence into `xrWaitFrame`](https://steamcommunity.com/app/250820/discussions/8/3001046778348674981/)
- [Unity XR display lifecycle](https://docs.unity3d.com/2022.3/Documentation/Manual/xrsdk-display.html)
- [OpenXR 1.1 spec](https://registry.khronos.org/OpenXR/specs/1.1/html/xrspec.html)
