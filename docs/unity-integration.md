# Integrating playreel

The package owns capture, microphone acquisition, log upload and bounded retirement. Your game owns consent UI, Steam initialization/callbacks, menus and its connection flow.

## Install

For local development, use Unity Package Manager's **Add package from disk** and select **packages/com.playreel.recording/package.json** in this checkout. Keep the checkout available while using the file dependency.

For Git installation, use:

~~~text
https://github.com/jacklaplante/playreel.git?path=/packages/com.playreel.recording#v0.1.0
~~~

Pin a reviewed commit or release tag. This source-first package contains no prebuilt runtime; build and bundle the native runtime separately.

If your game uses assembly definitions, put its adapter in a separate assembly referencing **Playreel.Client**, **Playreel.Gameplay** and **Playreel.Unity**. Add **Playreel.Steam** only if using its ticket adapter. Keep ordinary networking functional without the optional adapter/package.

## Consent and API

Provide a fresh unchecked required checkbox disclosing game-view/game-audio capture and diagnostic uploads. Microphone consent is a separate unchecked choice.

This sketch uses host-supplied deployment values, consent choices and an existing ticket owner:

~~~csharp
using Playreel.Client;
using Playreel.Gameplay;
using Playreel.Unity;
using UnityEngine;

var trust = new RecordingTrust(
    steamAppId, awsAccountId, awsRegion, issuerOrigin,
    streamNamePrefix, uploadBucket);
var profile = RecordingProfile.Standard().Freeze();
var factory = new UnityRecordingFactory(
    trust, tickets, readExistingSteamAppId, runtimeDirectory, profile);

bool began = recorder.Begin(
    requiredConsentAccepted, optionalMicrophoneConsentAccepted,
    factory, profile, runtimeDirectory, Application.version);
~~~

**recorder** is a **PlayreelRecorder** component on an active GameObject. The values, ticket provider and app-ID reader come from your adapter. The package does not discover trust from an API reply.

Require consent before Play/join. After required consent is accepted, let gameplay proceed regardless of recording startup success. Contain your adapter/configuration exceptions too; do not wait for tickets, native initialization or upload before releasing the game join.

Trust binds the exact API Gateway HTTPS origin, app, account, region, bucket and stream prefix. Never obtain those trusted values from player input or a response. Identity defaults are **prrec2:** / **playreel-recording:**; explicit compatibility settings must match on client and backend.

| API | Purpose |
| --- | --- |
| Begin | Explicit consent-owned start; false consent is refused |
| CanBegin | Earlier ownership must release before another start |
| Stop | End capture and retire bounded upload/cleanup |
| StopMicrophone | Stop listening and clear unmixed samples |
| GameGain / MicrophoneGain | Live 0–2 gains; cannot grant microphone consent |
| State / MediaReady / Failure | Read-only recording indicator |
| MicrophoneState / MicrophoneActive | Actual microphone readiness |
| LastHealthSnapshot | Bounded diagnostic observations |

Wire End, menu return and disconnect to **Stop**; keep the actual menu/game action in your adapter. Disabled/destroyed hosts transfer retirement to a bounded owner. Do not add a competing quit loop or dispose the game's Steam owner while ticket use is still draining.

## Steam ownership

**IRecordingTickets** lets you reuse a working game integration. **SteamRecordingTickets** can wrap a host-owned **SteamWebTicketClient**. The [Steamworks.NET template](../packages/com.playreel.recording/Samples~/SteamworksHost/README.md) shows the callback boundary.

Install Steamworks.NET in the host project if using that template. The package does not initialize/shut down Steam or replace the callback pump. Keep tickets out of logs and publisher keys only in the backend secret.

The template is opt-in source, not proof of live Steam admission in a new game. Validate app context, ownership and consent with your own app.

## Native runtime and player inclusion

Follow the [native guide](../native/README.md). Bundle the full validated runtime, including plugins/certificates/licenses, with the Windows player. Resolve its absolute directory from the application location, not the current working directory.

After consent, the factory leases process environment settings and supplies **PLAYREEL_AWS_REGION** from immutable trust. The loader requires the matching native capability; older DLLs are rejected. Keep environment restoration and native retirement together.

Player inclusion uses **PLAYTEST_RECORDING_ENABLED**. Editor compilation does not arm collection. A startup define, import or domain reload is not consent.

Verify disabled output separately: exclude your adapter, native runtime and recording-only Resources. Hiding a button does not remove build assets.

## Removing playreel

1. Remove your recording adapter and its dedicated resources. Preserve shared menus, fonts, Steam and ordinary networking.
2. Remove **com.playreel.recording** from package dependencies and resolve/recompile.
3. Stop copying native recording files into future builds and inspect actual player output.

The absent adapter must leave ordinary joins functional. Uninstallation does not destroy cloud resources, erase sessions or expire local downloads. Handle those separately using the [infrastructure guide](../infra/README.md).

## Proof boundaries

The independent sample tests inert import and lifecycle without devices/credentials. Generated native media and the opt-in IL2CPP load check are separate proofs. Each real integration still needs final rendered video/audio, menus, Steam, shutdown and deployment validation.
