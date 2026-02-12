# S&box Documentation Deep Dive - Comprehensive Research
## For Hexagon Roleplay Framework Development

> Research compiled from official Facepunch documentation, community guides, and open-source projects.
> Last updated: 2026-02-11

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Scene System & GameObjects](#2-scene-system--gameobjects)
3. [Component System](#3-component-system)
4. [Networking & Multiplayer](#4-networking--multiplayer)
5. [RPC Messages](#5-rpc-messages)
6. [Sync Properties](#6-sync-properties)
7. [Ownership & Authority](#7-ownership--authority)
8. [Network Events & Player Management](#8-network-events--player-management)
9. [UI System (Razor Panels)](#9-ui-system-razor-panels)
10. [Input System](#10-input-system)
11. [Data Persistence & Resources](#11-data-persistence--resources)
12. [Project & Addon Structure](#12-project--addon-structure)
13. [Player Controller](#13-player-controller)
14. [Prefabs](#14-prefabs)
15. [Code Generation (CodeGen)](#15-code-generation-codegen)
16. [Current Platform Status](#16-current-platform-status)
17. [Existing RP Projects & Libraries](#17-existing-rp-projects--libraries)
18. [Key Patterns for Roleplay Framework](#18-key-patterns-for-roleplay-framework)

---

## 1. Architecture Overview

S&box is built on Valve's Source 2 engine with a modern C# API. It uses a **scene-based architecture** (not map-based like classic Source Engine). Key characteristics:

- **Language**: C# with .NET 8.0
- **Scenes**: JSON files on disk, very fast to load and switch between
- **Hot-reload**: Code changes apply instantly without restart
- **Rendering**: Source 2 renderer
- **Physics**: Full 3D/2D physics system
- **UI**: Razor/Blazor-like HTML+C# system (not a real HTML renderer)
- **Networking**: Built-in multiplayer with Steam Networking
- **Open Source**: MIT License as of November 2025

The engine splits world creation into two layers:
- **Hammer (.vmap)**: Compiled geometry, terrain, baked lighting, navmesh
- **Scene Editor (.scene)**: Live-editable gameplay logic, GameObjects, scripts, lights, UI

---

## 2. Scene System & GameObjects

### Scenes

A scene is a collection of GameObjects. Scenes are the foundation of every project, saved as `.scene` files (JSON).

**Accessing the current scene:**
```csharp
// From any GameObject, Component, or Panel
var scene = Scene;

// Static access
var scene = Game.ActiveScene;
```

**Loading scenes:**
```csharp
// Replace current scene
Scene.Load(myNewScene);
Scene.LoadFromFile("scenes/minimal.scene");

// Additive loading (stack on top of existing)
var load = new SceneLoadOptions();
load.SetScene(myNewScene);
load.IsAdditive = true;
Scene.Load(load);
```

**Scene Directory (GUID-based lookup):**
```csharp
var obj = Scene.Directory.FindByGuid(guid);
```

**Component Lookup (fast indexed search):**
```csharp
// Iterate all components of a type
foreach (var model in Scene.GetAll<ModelRenderer>())
{
    model.Tint = Color.Random;
}

// Get a singleton instance
var game = Scene.Get<GameManager>();
```

### GameObjects

GameObjects are containers that hold Components. The formula:
`GameObject + Components = Game Entity`

Example: `GameObject + ModelRenderer + BoxCollider = Physical Object`

**Object access patterns:**
```csharp
// Get component on same object
var rb = GameObject.GetComponent<Rigidbody>();

// All scene instances of a type
foreach (var e in Scene.GetAll<EnemyAI>()) { ... }

// Hierarchy traversal
GameObject.Parent;
GameObject.Children;

// Tag checking
GameObject.Tags.Contains("Player");
```

### Hammer + Scene Workflow

```csharp
// Runtime map swapping
var map = Scene.GetAll<MapInstance>().First();
map.MapName = "maps/level_b.vmap";
map.Reload();
```

---

## 3. Component System

Components are behaviors attached to GameObjects. All game logic lives in Components.

### Lifecycle Methods (in order)

| Method | Description |
|--------|-------------|
| `OnLoad()` (async) | After deserialization; loading screen stays until complete |
| `OnValidate()` | When property changes in editor / after deserialization |
| `OnAwake()` | Once when created (if parent enabled), after loading |
| `OnStart()` | First time enabled, guaranteed before first OnFixedUpdate |
| `OnEnabled()` | Whenever component becomes enabled |
| `OnUpdate()` | Every frame |
| `OnPreRender()` | Every frame before rendering (after animation bones calculated; NOT on dedicated servers) |
| `OnFixedUpdate()` | Each fixed physics timestep |
| `OnDisabled()` | When component is disabled |
| `OnDestroy()` | When component is destroyed |

### Basic Component Example

```csharp
public partial class Rotator : Component
{
    [Property]
    public float Speed { get; set; } = 90f;

    protected override void OnUpdate()
    {
        GameObject.WorldRotation *= Rotation.FromYaw(Speed * Time.Delta);
    }
}
```

### Component Interfaces

| Interface | Purpose |
|-----------|---------|
| `ExecuteInEditor` | Run OnAwake/OnEnabled/OnUpdate/OnFixedUpdate in edit mode |
| `ICollisionListener` | OnCollisionStart, OnCollisionUpdate, OnCollisionStop |
| `ITriggerListener` | OnTriggerEnter, OnTriggerExit |
| `IDamageable` | OnDamage(in DamageInfo damage) |
| `INetworkListener` | Network connection events |
| `INetworkSpawn` | Network spawn events |

**Collision example:**
```csharp
public sealed class CollisionHandler : Component, Component.ICollisionListener
{
    public void OnCollisionStart(Collision other) { }
    public void OnCollisionUpdate(Collision other) { }
    public void OnCollisionStop(CollisionStop other) { }
}
```

**Damage example:**
```csharp
public sealed class Damageable : Component, Component.IDamageable
{
    public void OnDamage(in DamageInfo damage)
    {
        // Handle damage
    }
}
```

### Property Exposure

Use `[Property]` to expose fields in the Inspector:
```csharp
[Property] public float Speed { get; set; } = 200f;
[Property, Range(0, 100)] public int Health { get; set; } = 100;
[Property, TextArea] public string Description { get; set; }
```

---

## 4. Networking & Multiplayer

### Philosophy

The networking system is "purposefully simple and easy" -- designed to be easy to use and understand rather than bullet-proof server-authoritative.

### Lobby Management

```csharp
// Create a lobby
Networking.CreateLobby(new LobbyConfig()
{
    MaxPlayers = 8,
    Privacy = LobbyPrivacy.Public,
    Name = "My Lobby Name"
});

// Discover lobbies
var list = await Networking.QueryLobbies();

// Join a lobby
Networking.Connect(lobbyId);
```

### Networking GameObjects

Enable networking by setting a GameObject's Network Mode to "Network Object" in the editor.

```csharp
// Spawn a networked object
var go = PlayerPrefab.Clone(SpawnPoint.Transform.World);
go.NetworkSpawn();

// Spawn with ownership assigned to a connection
player.NetworkSpawn(connection);

// Remove a networked object
go.Destroy();
```

### IsProxy Pattern

The critical pattern for networking: check `IsProxy` to determine if someone else controls this object.

```csharp
protected override void OnUpdate()
{
    if (IsProxy) return; // Someone else controls this

    // Only the owner runs this code
    if (!Input.AnalogMove.IsNearZeroLength)
    {
        WorldPosition += Input.AnalogMove.Normal * Time.Delta * 100.0f;
    }
}
```

---

## 5. RPC Messages

RPCs (Remote Procedure Calls) are functions within Components that execute remotely across the network.

### RPC Types

| Attribute | Executes On |
|-----------|-------------|
| `[Rpc.Broadcast]` | All clients |
| `[Rpc.Owner]` | Only the networked object's owner (or host if no owner) |
| `[Rpc.Host]` | Only the host/server |

### Basic Examples

```csharp
[Rpc.Broadcast]
public void PlayOpenEffects()
{
    Sound.FromWorld("bing", WorldPosition);
}

// With parameters
[Rpc.Broadcast]
public void PlayOpenEffects(string soundName, Vector3 position)
{
    Sound.FromWorld(soundName, position);
}

// Static RPCs (on any static class)
[Rpc.Broadcast]
public static void PlaySoundAllClients(string soundName, Vector3 position)
{
    Sound.Play(soundName, position);
}
```

### Network Flags

```csharp
[Rpc.Broadcast(NetFlags.Unreliable | NetFlag.OwnerOnly)]
public void PlaySound(string soundName, Vector3 position)
{
    // Unreliable + only callable by owner
}
```

| Flag | Description |
|------|-------------|
| `NetFlags.Unreliable` | May not arrive; fastest and cheapest |
| `NetFlags.Reliable` | Default; ensures delivery with retries |
| `NetFlags.SendImmediate` | Not grouped; sent immediately |
| `NetFlags.DiscardOnDelay` | Drops unreliable messages if delayed |
| `NetFlag.HostOnly` | Only callable from host |
| `NetFlag.OwnerOnly` | Only callable by object owner |

### Filtering Recipients

```csharp
// Exclude specific players
using (Rpc.FilterExclude(c => c.DisplayName == "Harry"))
{
    PlayOpenEffects("bing", WorldPosition);
}

// Include only specific players
using (Rpc.FilterInclude(c => c.DisplayName == "Garry"))
{
    PlayOpenEffects("bing", WorldPosition);
}
```

### Authority Checks

```csharp
[Rpc.Broadcast]
public void PlayOpenEffects(string soundName, Vector3 position)
{
    if (!Rpc.Caller.IsHost) return;

    Log.Info($"{Rpc.Caller.DisplayName} with SteamID {Rpc.Caller.SteamId} played effects!");
    Sound.FromWorld(soundName, position);
}
```

`Rpc.Caller` gives you access to the calling connection's info (DisplayName, SteamId, IsHost).

---

## 6. Sync Properties

The `[Sync]` attribute automatically replicates property values to all players. Only the object owner can modify these properties.

### Basic Usage

```csharp
public class MyComponent : Component
{
    [Sync] public int Kills { get; set; }
    [Sync] public string PlayerName { get; set; }
    [Sync] public Vector3 TargetPosition { get; set; }
}
```

### Supported Types

- **Unmanaged types**: int, bool, float, Vector3, structs
- **Strings**
- **Special classes**: GameObject, Component, GameResource
- **Collections**: `NetList<T>`, `NetDictionary<K,V>`

### Change Callbacks

```csharp
[Sync, Change("OnIsRunningChanged")]
public bool IsRunning { get; set; }

private void OnIsRunningChanged(bool oldValue, bool newValue)
{
    // React to the change
}
```

**Limitation**: Callbacks fire only for direct property assignment, not collection content changes.

### Sync Flags

| Flag | Purpose |
|------|---------|
| `SyncFlags.Query` | Polls for changes each network update (for backing field modifications) |
| `SyncFlags.FromHost` | Host controls values, not object owner |
| `SyncFlags.Interpolate` | Smooths values across network ticks |

### Collections

```csharp
[Sync] public NetList<int> Scores { get; set; } = new();
[Sync] public NetDictionary<string, int> Inventory { get; set; } = new();
```

Initialize collections at declaration or on the owner. Non-owners may see null until networking creates them.

---

## 7. Ownership & Authority

### Core Concept

Ownership determines who simulates a networked GameObject. The owner controls position and variables; unowned objects are simulated by the host.

### Checking Ownership

```csharp
Log.Info($"Owner is {Network.OwnerId}");

// Check if someone else controls this
if (IsProxy) return;
```

### Taking & Dropping Ownership

```csharp
// Claim control
go.Network.TakeOwnership();

// Relinquish control (back to host)
Carrying.Network.DropOwnership();
```

### Owner Transfer Settings

```csharp
go.Network.SetOwnerTransfer(OwnerTransfer.Takeover);  // Anyone can change owner
go.Network.SetOwnerTransfer(OwnerTransfer.Fixed);      // Host-only (default)
go.Network.SetOwnerTransfer(OwnerTransfer.Request);    // Requires host approval
```

### Disconnection Handling (Orphaned Objects)

```csharp
GameObject.Network.SetOrphanedMode(NetworkOrphaned.Destroy);    // Delete (default)
GameObject.Network.SetOrphanedMode(NetworkOrphaned.Host);       // Host takes over
GameObject.Network.SetOrphanedMode(NetworkOrphaned.Random);     // Random client
GameObject.Network.SetOrphanedMode(NetworkOrphaned.ClearOwner); // Host simulates unowned
```

---

## 8. Network Events & Player Management

### INetworkListener Interface

```csharp
public sealed class GameNetworkManager : Component, Component.INetworkListener
{
    [Property] public GameObject PlayerPrefab { get; set; }
    [Property] public GameObject SpawnPoint { get; set; }

    // Client connected - starting handshake, loading game
    public void OnConnected(Connection connection) { }

    // Client fully connected, handshake complete
    public void OnActive(Connection connection)
    {
        var player = PlayerPrefab.Clone(SpawnPoint.Transform.World);

        var nameTag = player.Components.Get<NameTagPanel>(FindMode.EverythingInSelfAndDescendants);
        if (nameTag is not null)
        {
            nameTag.Name = connection.DisplayName;
        }

        player.NetworkSpawn(connection);
    }

    // Client disconnected
    public void OnDisconnected(Connection connection) { }
}
```

### INetworkSpawn Interface

```csharp
public sealed class MyNetworkedComponent : Component, Component.INetworkSpawn
{
    public void OnNetworkSpawn(Connection owner)
    {
        // Called when this object is spawned on the network
    }
}
```

### NetworkHelper Component

A built-in helper component that handles common multiplayer patterns:
- Auto-creates server on scene load (if StartServer enabled)
- Spawns players from PlayerPrefab on connection
- Supports spawn point system (random selection from list)
- Implements INetworkListener internally

### Connection Properties

- `connection.DisplayName` - Player's display name
- `connection.SteamId` - Player's Steam ID
- `Rpc.Caller.IsHost` - Whether the RPC caller is the host

---

## 9. UI System (Razor Panels)

### Architecture

S&box UI uses Razor syntax (like Blazor) as a convenience layer. Panels are NOT rendered with an HTML renderer -- they use a custom rendering system that looks like HTML/CSS.

Key rule: Every UI needs a `PanelComponent` at the root, attached to a GameObject with `ScreenPanel` (2D HUD) or `WorldPanel` (3D world-space UI).

### PanelComponent (Root UI)

```csharp
@using Sandbox;
@using Sandbox.UI;
@inherits PanelComponent

<root>
    <div class="title">@MyStringValue</div>
</root>

@code
{
    [Property, TextArea] public string MyStringValue { get; set; } = "Hello World!";

    // Determines when to rebuild - include ALL reactive values
    protected override int BuildHash() => System.HashCode.Combine(MyStringValue);
}
```

### Child Panel (Reusable)

```csharp
@using Sandbox;
@using Sandbox.UI;

<root>
    <div class="health">HP: @Health</div>
    <div class="armor">Armor: @Armor</div>
</root>

@code
{
    public int Health { get; set; } = 100;
    public int Armor { get; set; } = 100;

    protected override int BuildHash() => System.HashCode.Combine(Health, Armor);
}
```

### C# in Markup

```html
<root>
    @foreach(var player in Player.All)
    {
        <div class="player">
            <label>@player.Name</label>
            @if(player.IsDead)
            {
                <img src="ui/skull.png" />
            }
        </div>
    }
</root>
```

### Data Binding

```csharp
<!-- Two-way binding -->
<SliderEntry min="0" max="100" step="1" Value:bind=@IntValue />

<!-- Passing properties to children -->
<MyChildPanel Health=@(30) Armor=@(75) />

<!-- Panel references -->
<root>
    <MyChildPanel @ref="PanelReference" />
</root>

@code
{
    MyChildPanel PanelReference { get; set; }

    protected override void OnStart()
    {
        PanelReference.Health = Player.Local.Health;
    }
}
```

### Razor Components (Reusable Slots)

```csharp
// InfoCard.razor - Define slots
<root>
    <div class="header">@Header</div>
    <div class="body">@Body</div>
    <div class="footer">@Footer</div>
</root>

@code
{
    public RenderFragment Header { get; set; }
    public RenderFragment Body { get; set; }
    public RenderFragment Footer { get; set; }
}

// Usage
<InfoCard>
    <Header>Character Info</Header>
    <Body>
        <div class="name">John Doe</div>
    </Body>
    <Footer>
        <div class="button" onclick="@Close">Close</div>
    </Footer>
</InfoCard>
```

### Parameterized Fragments

```csharp
public RenderFragment<Player> PlayerRow { get; set; }

// Usage
<PlayerRow Context="item">
    <div class="row">@item.Name</div>
</PlayerRow>
```

### Generic Components

```csharp
@typeparam T

<MyPanel T="string"></MyPanel>
```

### BuildHash (Performance)

Panels only rebuild when `BuildHash()` returns a different value:
```csharp
protected override int BuildHash() => System.HashCode.Combine(MyStringValue, Health, Armor);
```

Force rebuild: `StateHasChanged()`

### PanelComponent vs Panel

- **PanelComponent**: Has component lifecycle (OnStart, OnUpdate), uses `Panel.Style.Left = Length.Auto;`
- **Panel**: Uses `OnAfterTreeRender(bool firstTime)` and `Tick()` instead
- **Critical**: PanelComponents cannot be nested inside other Panels/PanelComponents

### Styling (CSS-like)

S&box UI uses a CSS-like styling system with flexbox as default layout:

```scss
MyPanel {
    display: flex;
    flex-direction: column;
    background-color: rgba(0, 0, 0, 0.8);
    border-radius: 8px;
    padding: 16px;
    font-family: Poppins;
    font-size: 14px;
    color: white;

    transition: all 0.2s ease;

    // Pseudo-classes
    &:hover {
        background-color: rgba(255, 255, 255, 0.1);
    }

    // Enter/exit animations
    &:intro {
        transform: scale(0);
        opacity: 0;
    }
    &:outro {
        transform: scale(2);
        opacity: 0;
    }
}
```

**Custom properties**: `sound-in`, `sound-out`, `background-image-tint`, `text-stroke`

**Supported units**: px, %, em, vw, deg

**Filters**: blur, saturate, contrast, brightness, grayscale, sepia, hue-rotate, invert

---

## 10. Input System

### Core Input Class

```csharp
// Button states (string-based action names, case insensitive)
Input.Down("jump")      // Key is held down
Input.Pressed("jump")   // Key just pressed this frame
Input.Released("jump")  // Key just released this frame

// Analog input
Input.AnalogMove   // Vector3 - WASD/joystick movement
Input.AnalogLook   // Vector3 - Mouse/joystick look
```

### Basic Player Input

```csharp
public sealed class MyPlayerComponent : Component
{
    protected override void OnUpdate()
    {
        if (Input.Down("jump"))
        {
            WorldPosition += Vector3.Forward * Time.Delta;
        }

        if (Input.Pressed("Attack1"))
        {
            BulletPrefab.Clone(WorldPosition);
        }
    }
}
```

### Escape Key Override

```csharp
if (Input.EscapePressed)
{
    Input.EscapePressed = false;
    // Custom escape handling (e.g., close menu instead of pause)
}
```

### Custom Bindings

Configured through **Project Settings** in the editor. Map action names to physical keys.

---

## 11. Data Persistence & Resources

### GameResource System

GameResource is a base class for creating custom data asset types that can be edited in the s&box editor.

```csharp
[GameResource("Weapon Definition", "weapon", "Defines a weapon type")]
public class WeaponResource : GameResource
{
    public string DisplayName { get; set; }
    public float Damage { get; set; }
    public float FireRate { get; set; }
    public GameObject ProjectilePrefab { get; set; }
}
```

- Creates `.weapon` files editable in the asset browser
- Can be referenced in Components via `[Property]`
- Synced types for [Sync] properties
- Supports inheritance (with known issues around subclass assignment)

### File System

S&box provides `FileSystem.Data` for local file operations. Data is saved to the game's data folder.

### Sandbank (Community Database)

Sandbank is a community-built no-SQL database using JSON files:

```csharp
// Define a document class
public class PlayerData
{
    public string UID { get; set; }  // Required unique identifier
    public string Name { get; set; }
    public int Money { get; set; }
    public string Faction { get; set; }
}

// Save
Sandbank.Insert<PlayerData>("players", playerData);

// Load by ID
var player = Sandbank.FindById<PlayerData>("players", steamId);

// Complex queries
var richPlayers = Sandbank.Select<PlayerData>("players", x => x.Money > 10000);
```

### WebSocket Persistence

For cloud/cross-server persistence:

```csharp
public sealed class DataServer : Component
{
    [Property] public string ConnectionUri { get; set; }
    public WebSocket Socket { get; set; }

    protected override void OnStart()
    {
        Socket = new WebSocket();
        Socket.OnMessageReceived += HandleMessageReceived;
        _ = Connect();
    }

    private async Task Connect()
    {
        // Auth token for secure connections
        var token = await Sandbox.Services.Auth.GetToken("YourServiceName");
        var headers = new Dictionary<string, string>()
        {
            { "Authorization", token }
        };
        await Socket.Connect(ConnectionUri, headers);
    }

    private async Task SendMessage(string message)
    {
        await Socket.Send(message);
    }

    private void HandleMessageReceived(string message)
    {
        Log.Info(message);
    }
}
```

---

## 12. Project & Addon Structure

### Recommended Directory Structure

```
/Code          - C# scripts and components
/Assets        - All game content
  /maps        - Hammer .vmap files
  /materials   - .vmat files
  /models      - .vmdl compiled models
  /prefabs     - Reusable .prefab blueprints
  /scenes      - .scene logic layers
  /sounds      - .sound assets
  /textures    - Source texture files
  /ui          - UI razor files and stylesheets
```

### Addon Projects

- Addons enhance a Game Project by using its components and assets
- Addon projects are NOT published directly; individual assets are published
- Cannot contain code yet (only ActionGraph)
- Target a specific game in project settings
- Assets are published individually to sbox.game

### Publishing

Projects publish to **sbox.game** (not Steam Workshop):
1. Sign in at sbox.game with Steam
2. Go to "My Creations"
3. Upload project with title, thumbnail, description
4. Press "Publish"

---

## 13. Player Controller

### Built-in PlayerController

A first/third person controller that works as a specialized RigidBody with built-in input handling:

- Physics-based movement (velocity, mass)
- Built-in input support (keyboard, mouse, gamepad)
- Camera system (first/third person switching)
- Animator for Citizen AnimGraph assets
- Use/interact system

### Controlling via Code

```csharp
protected override void OnFixedUpdate()
{
    // Set desired movement direction
    WishVelocity = Input.AnalogMove.Normal * Speed;

    // Set camera angles
    EyeAngles += Input.AnalogLook;
}
```

### Event System (IEvents Interface)

Components can listen to PlayerController events:

| Event | Description |
|-------|-------------|
| `OnEyeAngles()` | Modify camera sensitivity |
| `PostCameraSetup()` | Adjust camera post-initialization |
| `OnJumped()` | Jump notification |
| `OnLanded()` | Landing with fall distance and impact velocity |
| `GetUsableComponent()` | Identify interactable objects |
| `StartPressing()/StopPressing()` | Usage state tracking |
| `FailPressing()` | Failed interaction feedback |

---

## 14. Prefabs

Reusable GameObjects for multi-scene usage or runtime instantiation.

### Creating
Right-click GameObject > "Convert to Prefab" -- saves as a PrefabFile asset.

### Runtime Spawning

```csharp
[Property]
GameObject BulletPrefab { get; set; }

protected override void OnUpdate()
{
    if (Input.Pressed("Attack1"))
    {
        GameObject bullet = BulletPrefab.Clone(WorldPosition);
        bullet.BreakFromPrefab(); // Optional: sever prefab link
    }
}
```

### Prefab Templates
Enable "Show In Menu" on a PrefabFile to add it to the Create menu. `DontBreakAsTemplate` controls whether instances maintain their prefab link.

---

## 15. Code Generation (CodeGen)

CodeGen intercepts and wraps methods/properties using the `[CodeGenerator]` attribute.

### Uses in s&box

1. **RPCs**: Wraps methods to transmit network messages when called
2. **Networked Variables ([Sync])**: Intercepts property get/set to sync across network
3. **Method wrapping**: Uses `WrappedMethod` to pause/resume execution
4. **Property interception**: `WrappedPropertyGet<T>` and `WrappedPropertySet<T>`

This is what makes `[Sync]` and `[Rpc.Broadcast]` work under the hood.

---

## 16. Current Platform Status

### Complete (Green)
- Audio (sounds, music, voice chat, lip sync)
- Multiplayer (lobbies, sync, RPCs, networked objects)
- UI (Razor panels, stylesheets, transitions, animations)
- Scenes, Navigation, Physics, Editor
- Hammer/Maps
- Animation (AnimGraph, IK, ragdolls, morphs)
- Post-Processing & Particles

### In Progress (Orange)
- Dedicated server support (missing Linux, error handling)
- VR (functional but needs polish)
- Controller (needs UI navigation, on-screen keyboard)
- Terrain (early development)
- Standalone Export (licensing, exporter in dev)

### Not Yet Implemented (Red)
- 2D editor mode
- Grass/detail models for terrain

---

## 17. Existing RP Projects & Libraries

### Roleplay Gamemodes
- **YourRP** - https://github.com/d4kir92/sbox-yourrp - Generic RP gamemode
- **Raven** - https://github.com/FlorianLeChat/sbox-raven - Sandbox-based RP test

### Useful Libraries
- **Sandbank** - https://github.com/anthonysharpy/sandbank - Fast no-SQL database
- **Advisor** - https://github.com/game-creators-area/Advisor - Administration framework
- **Simple Weapon Base** - https://github.com/timmybo5/simple-weapon-base - Weapon framework

### Reference Gamemodes (by Facepunch)
- **Sandbox** - https://github.com/Facepunch/sandbox - "Garry's Mod in S&Box"
- **sbox-minimal** - https://github.com/Facepunch/sbox-minimal - Minimal template
- **Hidden** - https://github.com/Facepunch/sbox-hidden - Team-based gamemode
- **DM98** - https://github.com/Facepunch/dm98 - Classic deathmatch

---

## 18. Key Patterns for Roleplay Framework

Based on this research, here are the critical patterns for building a NutScript/Helix-like roleplay framework:

### Game Manager Pattern
```csharp
public sealed class GameManager : Component, Component.INetworkListener
{
    [Property] public GameObject PlayerPrefab { get; set; }
    [Property] public List<GameObject> SpawnPoints { get; set; }

    public void OnActive(Connection connection)
    {
        var spawnPoint = SpawnPoints[Random.Shared.Next(SpawnPoints.Count)];
        var player = PlayerPrefab.Clone(spawnPoint.Transform.World);
        player.NetworkSpawn(connection);
    }

    public void OnDisconnected(Connection connection)
    {
        // Save player data, cleanup
    }
}
```

### Character Data Sync Pattern
```csharp
public class CharacterComponent : Component
{
    [Sync] public string CharacterName { get; set; }
    [Sync] public string Description { get; set; }
    [Sync] public string Faction { get; set; }
    [Sync] public int Money { get; set; }
    [Sync] public NetDictionary<string, int> Attributes { get; set; } = new();

    [Rpc.Host]
    public void RequestSetMoney(int amount)
    {
        if (!Rpc.Caller.IsHost && Rpc.Caller.SteamId != Network.OwnerId)
            return;
        Money = amount;
    }
}
```

### Inventory via Sync Collections
```csharp
public class InventoryComponent : Component
{
    [Sync] public NetList<string> Items { get; set; } = new();
    [Sync] public int MaxSlots { get; set; } = 20;

    [Rpc.Host]
    public void RequestAddItem(string itemId)
    {
        if (Items.Count >= MaxSlots) return;
        Items.Add(itemId);
    }
}
```

### UI HUD Pattern
```csharp
@using Sandbox;
@using Sandbox.UI;
@inherits PanelComponent

<root>
    <div class="hud">
        <div class="health-bar" style="width: @(Health)%"></div>
        <div class="money">$@Money</div>
        <div class="name">@CharacterName</div>
    </div>
</root>

@code
{
    public int Health => LocalCharacter?.Health ?? 100;
    public int Money => LocalCharacter?.Money ?? 0;
    public string CharacterName => LocalCharacter?.CharacterName ?? "";

    CharacterComponent LocalCharacter =>
        Scene.GetAll<CharacterComponent>().FirstOrDefault(c => !c.IsProxy);

    protected override int BuildHash() =>
        System.HashCode.Combine(Health, Money, CharacterName);
}
```

### Data Persistence Pattern (with Sandbank)
```csharp
public class PlayerData
{
    public string UID { get; set; }  // SteamId
    public string LastCharacterName { get; set; }
    public string Faction { get; set; }
    public int Money { get; set; }
    public string InventoryJson { get; set; }
}

// On player join
public void OnActive(Connection connection)
{
    var data = Sandbank.FindById<PlayerData>("players", connection.SteamId.ToString());
    if (data == null)
    {
        data = new PlayerData { UID = connection.SteamId.ToString() };
        Sandbank.Insert<PlayerData>("players", data);
    }
    // Apply data to spawned character...
}

// On player leave
public void OnDisconnected(Connection connection)
{
    // Save character data to Sandbank
}
```

### Admin Command Pattern
```csharp
[Rpc.Host]
public static void AdminSetMoney(GameObject target, int amount)
{
    // Verify caller is admin
    if (!IsAdmin(Rpc.Caller.SteamId)) return;

    var character = target.Components.Get<CharacterComponent>();
    if (character != null)
        character.Money = amount;
}
```

---

## Documentation Sources

- Facepunch Official Docs: https://docs.facepunch.com/s/sbox-dev
- S&box Developer Portal: https://sbox.game/dev/
- Steam Community Guide: https://steamcommunity.com/sharedfiles/filedetails/?id=3595903475
- GitHub (sbox-public): https://github.com/Facepunch/sbox-public
- Sandbank Database: https://github.com/anthonysharpy/sandbank
- YourRP Gamemode: https://github.com/d4kir92/sbox-yourrp
- Awesome S&box: https://github.com/Ryhon0/awesome-sbox
