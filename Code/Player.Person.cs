#nullable enable

using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

// A character is also something other characters act on. What can be done to one depends on the
// state anyone can see it is in, and every such act goes through RequestAct like any other.
public sealed partial class Player
{
	public const string IntroduceVerb = "person.introduce";
	public const string RestrainVerb = "person.restrain";
	public const string ReleaseVerb = "person.release";
	public const string SearchVerb = "person.search";
	public const string ReviveVerb = "person.revive";
	public const string FinishVerb = "person.finish";

	public IReadOnlyList<Verb> Verbs
	{
		get
		{
			if ( IsDown )
			{
				var finish = GameManager.Instance?.FinishRequires is { Length: > 0 } needed ? needed : null;
				return new[] { new Verb( ReviveVerb, "Help up" ), new Verb( SearchVerb, "Search" ), new Verb( FinishVerb, "Finish", finish ) };
			}
			if ( IsRestrained ) return new[] { new Verb( SearchVerb, "Search" ), new Verb( ReleaseVerb, "Release" ) };
			return new[] { new Verb( IntroduceVerb, "Introduce yourself" ), new Verb( RestrainVerb, "Restrain", Capability.Restrain ) };
		}
	}

	float IVerbTarget.Reach => 120f;

	bool IPressable.CanPress( IPressable.Event e ) => IsProxy && HasCharacter;

	bool IPressable.Press( IPressable.Event e )
	{
		Local?.RequestAct( this, Verbs[0].Id );
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) =>
		Local is null ? null : new IPressable.Tooltip( Local.LabelFor( this ), "person", CharacterDescription );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		if ( _character is null || actor.HostCharacter is not { } other || actor == this )
			return Result.Fail( ErrorCode.Invalid, "There is nobody there." );
		switch ( verb.Id )
		{
			case IntroduceVerb: return HostIntroduce( actor, other );
			case ReviveVerb: return HostRevive();
			case SearchVerb: return HostBeSearched( actor );
			case RestrainVerb: return HostBeRestrained( actor, other );
			case ReleaseVerb: return HostSetRestrained( false, "Your hands are free." );
			case FinishVerb:
				var can = Vitals.CanFinish( _character );
				if ( can.Ok ) HostDie( "finished", actor );
				return can;
			default: return Result.Fail( ErrorCode.Invalid, "That cannot be done." );
		}
	}

	private Result HostIntroduce( Player actor, CharacterData other )
	{
		var told = Recognition.Introduce( other, _character! );
		if ( !told.Ok ) return told;
		GameManager.Instance?.Roster?.Save( _character! );
		SendPrivateState();
		if ( Network.Owner is { } owner ) Chat.Tell( owner, $"{Recognition.Stranger( other.Description )} is {other.Name}." );
		if ( actor.Network.Owner is { } teller ) Chat.Tell( teller, "You introduce yourself." );
		return told;
	}

	/// <summary>Searching is opening someone's inventory as a holder, for as long as they cannot stop it.</summary>
	private Result HostBeSearched( Player actor )
	{
		if ( !IsIncapable ) return Result.Fail( ErrorCode.Conflict, "They would not let you." );
		actor.HostOpen( this, _character!, "Belongings", onlyWhile: () => IsIncapable && _character is not null );
		if ( Network.Owner is { } owner ) Chat.Tell( owner, "Someone is going through your belongings." );
		return Result.Success();
	}

	private Result HostBeRestrained( Player actor, CharacterData other )
	{
		if ( IsRestrained ) return Result.Fail( ErrorCode.Conflict, "They are already restrained." );
		// The tie is used up: a restraint is cut off, not untied.
		var tie = other.Inventory.Items.FirstOrDefault( item => ItemDefinition.Find( item.Definition )?.Grants.Contains( Capability.Restrain ) == true );
		if ( tie is null ) return Result.Fail( ErrorCode.Denied, "You have nothing to bind them with." );
		var game = GameManager.Instance!;
		game.Transfers!.Destroy( Sinks.Consumed, other, tie.Id, Actor.Of( other ), count: 1 );
		game.Roster!.Save( other );
		actor.SendPrivateState();
		return HostSetRestrained( true, "Your hands are bound." );
	}

	private Result HostSetRestrained( bool restrained, string message )
	{
		_character!.IsRestrained = restrained;
		if ( restrained ) _character.Equipped = null;
		GameManager.Instance?.Roster?.Save( _character );
		HostClose();
		HostSyncVitals();
		SendPrivateState();
		if ( Network.Owner is { } owner ) Chat.Tell( owner, message );
		return Result.Success();
	}
}
