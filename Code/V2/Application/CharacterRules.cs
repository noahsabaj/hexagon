#nullable enable

using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Application;

public static class CharacterRules
{
	public const int MaximumNameLength = 64;
	public const int MinimumNameLength = 3;
	public const int MaximumDescriptionLength = 512;
	public const int MinimumDescriptionLength = 16;

	/// <summary>
	/// Hard per-account slot cap. Slots are dense in 0..MaximumSlots-1 (commit-time
	/// invariant), which lets account listings run as bounded keyed slot probes
	/// instead of scanning every character ever created.
	/// </summary>
	public const int MaximumSlots = 64;

	public static OperationResult ValidateCreationRequest( CharacterCreationRequest request, IReadOnlySet<string> allowedFields )
	{
		if ( request.Name.Trim().Length is < MinimumNameLength or > MaximumNameLength )
			return OperationResult.Failure( ErrorCode.InvalidArgument, $"Name must be {MinimumNameLength}-{MaximumNameLength} characters." );

		if ( request.Description.Trim().Length is < MinimumDescriptionLength or > MaximumDescriptionLength )
			return OperationResult.Failure( ErrorCode.InvalidArgument, $"Description must be {MinimumDescriptionLength}-{MaximumDescriptionLength} characters." );

		foreach ( var field in request.Fields.Keys )
		{
			if ( !allowedFields.Contains( field ) )
				return OperationResult.Failure( ErrorCode.InvalidArgument, $"Unknown or non-creation field '{field}'." );
		}

		return OperationResult.Success();
	}

	/// <summary>
	/// Lowest unoccupied slot for the account, or -1 when all
	/// <see cref="MaximumSlots"/> slots are occupied.
	/// </summary>
	public static int FindLowestFreeSlot( IEnumerable<CharacterRecord> characters )
	{
		var occupied = characters.Select( character => character.Slot ).Where( slot => slot >= 0 ).ToHashSet();
		var slot = 0;
		while ( occupied.Contains( slot ) ) slot++;
		return slot < MaximumSlots ? slot : -1;
	}
}
