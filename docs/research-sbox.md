# S&box Development: Complete Research

## Platform Overview
- Source 2 engine by Facepunch Studios
- C# 14 / .NET 10 (no Lua)
- MIT open source (Nov 2025), April 2026 release
- Scene + Component architecture (like Unity)
- Hot-reload, Razor UI, built-in networking

## Scene System
```csharp
Scene.Load(myScene);
Scene.LoadFromFile("scenes/minimal.scene");
// Additive loading
Scene.Load(new SceneLoadOptions { IsAdditive = true, ... });
// Discovery
Scene.GetAll<T>(); Scene.Get<T>(); Scene.Directory.FindByGuid(guid);
```

## Component System
```csharp
public partial class MyComponent : Component
{
    [Property] public float Speed { get; set; } = 200f;

    protected override void OnStart() { }       // Once on first enable
    protected override void OnUpdate() { }      // Every frame
    protected override void OnFixedUpdate() { } // Physics tick
    protected override void OnDestroy() { }     // Cleanup
    protected override void OnEnabled() { }
    protected override void OnDisabled() { }
}
```

### Interfaces
- ICollisionListener: OnCollisionStart/Update/Stop
- ITriggerListener: OnTriggerEnter/Exit
- IDamageable: OnDamage
- INetworkListener: OnConnected/OnActive/OnDisconnected
- INetworkSpawn: OnNetworkSpawn

## Networking

### Sync Properties
```csharp
[Sync] public int Health { get; set; }
[Sync] public string Name { get; set; }
[Sync] public NetList<string> Items { get; set; } = new();
[Sync] public NetDictionary<string, int> Stats { get; set; } = new();

[Sync, Change("OnHealthChanged")]
public int Health { get; set; }
```

### RPCs
```csharp
[Rpc.Broadcast] public void PlayEffect() { }    // All clients
[Rpc.Host] public void RequestAction() { }       // Host only
[Rpc.Owner] public void NotifyOwner() { }        // Owner only

// Inside RPC: Rpc.Caller.SteamId, Rpc.Caller.IsHost, Rpc.Caller.DisplayName
// Filtering: Rpc.FilterInclude(...), Rpc.FilterExclude(...)
```

### IsProxy Pattern (CRITICAL)
```csharp
protected override void OnUpdate()
{
    if (IsProxy) return; // Someone else owns this, skip
    // Only owner runs this code
}
```

### Ownership
```csharp
go.NetworkSpawn(connection);  // Spawn with owner
go.Network.TakeOwnership();
go.Network.DropOwnership();
go.Network.SetOwnerTransfer(OwnerTransfer.Fixed/Takeover/Request);
go.Network.SetOrphanedMode(NetworkOrphaned.Destroy/Host/Random/ClearOwner);
```

### Player Connection
```csharp
public class GameManager : Component, Component.INetworkListener
{
    [Property] public GameObject PlayerPrefab { get; set; }

    public void OnActive(Connection connection)  // Fully connected
    {
        var player = PlayerPrefab.Clone(spawnPos);
        player.NetworkSpawn(connection);
    }

    public void OnDisconnected(Connection connection) { }
}
```

## UI System (Razor)
```razor
@using Sandbox;
@using Sandbox.UI;
@inherits PanelComponent

<root>
    <div class="hud">@Health HP</div>
</root>

@code {
    [Property] public int Health { get; set; }
    protected override int BuildHash() => HashCode.Combine(Health);
}
```

- ScreenPanel for HUD, WorldPanel for 3D UI
- CSS-like styling with flexbox, transitions, animations
- BuildHash() controls when panel rebuilds

## Persistence

### Sandbank (Community No-SQL)
```csharp
public class PlayerData
{
    public string UID { get; set; }  // Required
    public string Name { get; set; }
    public int Money { get; set; }
}

Sandbank.Insert<PlayerData>("players", data);
Sandbank.FindById<PlayerData>("players", steamId);
Sandbank.Select<PlayerData>("players", x => x.Money > 1000);
```

### WebSocket (External DB)
```csharp
var token = await Sandbox.Services.Auth.GetToken("MyService");
var socket = new WebSocket();
await socket.Connect(uri, headers);
```

### Auth Token Validation
POST to `https://services.facepunch.com/sbox/auth/token` with {steamid, token}

## GameResource (Custom Asset Types)
```csharp
[GameResource("Weapon Definition", "weapon", "Defines a weapon")]
public class WeaponResource : GameResource
{
    public string DisplayName { get; set; }
    public float Damage { get; set; }
}
```

## Input
```csharp
Input.Down("jump")       // Held
Input.Pressed("attack1") // Just pressed
Input.Released("use")    // Just released
Input.AnalogMove         // WASD Vector3
Input.AnalogLook         // Mouse delta Vector3
```

## Project Structure
```
/Code        -- C# scripts
/Assets
  /maps, /materials, /models, /prefabs, /scenes, /sounds, /textures, /ui
```

## Security Sandbox
- Whitelisted namespaces only
- No System.IO (use FileSystem.Data)
- Limited reflection
- Prevents arbitrary host access

## Existing RP Projects (ALL DarkRP-style, no Helix equivalent)
- DXRP (most polished, DarkRP reimagined)
- DarkRP by sousou63 (open source, MIT)
- CivitasRP (Arma 3 Life inspired)
- YourRP

## Strategic Insight
**No NutScript/Helix equivalent exists for s&box.** This is a completely unoccupied niche.
