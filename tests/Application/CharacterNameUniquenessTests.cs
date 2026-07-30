using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class CharacterNameUniquenessTests
{
	[TestMethod]
	public async Task ExactModeRefusesAPlainRepeatButAllowsALookAlike()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var service = environment.CreateCharacterService(
			nameUniqueness: CharacterRules.NameUniqueness.Exact );

		Assert.IsTrue( (await service.CreateAsync( new AccountId( 8100 ),
			ApplicationServiceTestEnvironment.Request() with { Name = "Alyx Vance" } )).Succeeded );

		// Case, accents, spacing and punctuation still do not make a new identity.
		var repeat = await service.CreateAsync( new AccountId( 8101 ),
			ApplicationServiceTestEnvironment.Request() with { Name = "alyx  vance" } );
		Assert.IsTrue( repeat.Failed );
		Assert.AreEqual( ErrorCode.Conflict, repeat.Error!.Code );

		// But a confusable spelling is allowed, which is the whole point of loosening.
		Assert.IsTrue( (await service.CreateAsync( new AccountId( 8102 ),
			ApplicationServiceTestEnvironment.Request() with { Name = "A1yx Vance" } )).Succeeded );
	}

	[TestMethod]
	public async Task NoneModeLetsNamesRepeatFreely()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var service = environment.CreateCharacterService(
			nameUniqueness: CharacterRules.NameUniqueness.None );

		Assert.IsTrue( (await service.CreateAsync( new AccountId( 8200 ),
			ApplicationServiceTestEnvironment.Request() with { Name = "Alyx Vance" } )).Succeeded );
		Assert.IsTrue( (await service.CreateAsync( new AccountId( 8201 ),
			ApplicationServiceTestEnvironment.Request() with { Name = "Alyx Vance" } )).Succeeded );
		Assert.IsEmpty( environment.Repositories.UniqueReservations.All()
			.Where( document => document.Value.Namespace == "hexagon.character-name" ).ToArray() );
	}

	[TestMethod]
	public async Task EnablingUniquenessProtectsNamesThatPredateIt()
	{
		// The upgrade path, and the one an existing server actually meets: run with uniqueness off,
		// accumulate characters, then turn it on. Those characters hold no reservation, because a
		// reservation is only ever written at creation. Deciding from the reservation index would
		// therefore leave exactly them - the oldest names on the server, typically the operators'
		// own - free for anyone to take.
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var beforeUpgrade = environment.CreateCharacterService(
			nameUniqueness: CharacterRules.NameUniqueness.None );
		Assert.IsTrue( (await beforeUpgrade.CreateAsync( new AccountId( 8300 ),
			ApplicationServiceTestEnvironment.Request() with { Name = "Alyx Vance" } )).Succeeded );
		Assert.IsEmpty( environment.Repositories.UniqueReservations.All()
			.Where( document => document.Value.Namespace == "hexagon.character-name" ).ToArray() );

		var afterUpgrade = environment.CreateCharacterService(
			nameUniqueness: CharacterRules.NameUniqueness.Skeleton );
		var impersonation = await afterUpgrade.CreateAsync( new AccountId( 8301 ),
			ApplicationServiceTestEnvironment.Request() with { Name = "A1yx Vance" } );
		Assert.IsTrue( impersonation.Failed );
		Assert.AreEqual( ErrorCode.Conflict, impersonation.Error!.Code );
	}

	[TestMethod]
	public async Task TighteningFromExactToSkeletonProtectsNamesReservedUnderTheLooserScheme()
	{
		// The same drift without the feature ever being off. A reservation stores the key the
		// scheme in force produced, and tightening does not rewrite it.
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var loose = environment.CreateCharacterService(
			nameUniqueness: CharacterRules.NameUniqueness.Exact );
		Assert.IsTrue( (await loose.CreateAsync( new AccountId( 8400 ),
			ApplicationServiceTestEnvironment.Request() with { Name = "A1yx Vance" } )).Succeeded );
		// Stored unfolded, so the digit survives in the key.
		Assert.AreEqual( "a1yxvance", environment.Repositories.UniqueReservations.All()
			.Single( document => document.Value.Namespace == "hexagon.character-name" ).Value.Value );

		var tight = environment.CreateCharacterService(
			nameUniqueness: CharacterRules.NameUniqueness.Skeleton );
		var lookAlike = await tight.CreateAsync( new AccountId( 8401 ),
			ApplicationServiceTestEnvironment.Request() with { Name = "Alyx Vance" } );
		Assert.IsTrue( lookAlike.Failed );
		Assert.AreEqual( ErrorCode.Conflict, lookAlike.Error!.Code );
	}

	[TestMethod]
	public void LooseningNeverWeakensWhatMakesANameWellFormed()
	{
		// The setting governs how CLOSE two names may be. Whether a name is well-formed at all is
		// a persistence invariant and must stay fixed, or tightening the setting later would make
		// an existing store fail its own startup validation with no rename path out.
		Assert.IsTrue( CharacterRules.NormalizeName( "Bob\u202Eelbat" ).Failed );
		Assert.IsTrue( CharacterRules.NormalizeName( "Bob\u0007Smith" ).Failed );
		Assert.IsTrue( CharacterRules.NormalizeName( "\u0410lice" ).Failed );
	}

	[TestMethod]
	public void ExactReductionStaysUsableAsAReservationKey()
	{
		// Reservation keys reject '/', '\' and ':', so the loosened key must still yield only
		// letters and digits rather than echoing the raw name.
		var key = CharacterNameSkeleton.Exact( "Dr. Judith Mossman: C17/A" );
		Assert.IsNotEmpty( key );
		Assert.IsFalse( key.Any( character => character is '/' or '\\' or ':' ) );
		Assert.IsTrue( key.All( char.IsLetterOrDigit ) );
		// Unfolded, so a digit stays a digit rather than becoming the letter it imitates.
		Assert.AreNotEqual( CharacterNameSkeleton.Exact( "A1ice" ), CharacterNameSkeleton.Exact( "Alice" ) );
		Assert.AreEqual( CharacterNameSkeleton.Of( "A1ice" ), CharacterNameSkeleton.Of( "Alice" ) );
	}
}