#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Hexagon.V2.Tests.Architecture;

/// <summary>
/// Admission-completeness guard for the client-to-host RPC surface. The engine applies no rate
/// limiting of its own, so Hexagon's per-connection budget is the only limiter that exists: an
/// [Rpc.Host] method that does not route through it is unmetered work any client can drive at
/// whatever rate the transport allows. Detection is not enough here - the one endpoint that was
/// unmetered got that way by being added without anyone noticing - so this fails the suite until
/// a new entry point is either charged or reviewed onto the list below with a reason.
/// </summary>
[TestClass]
public sealed class RpcAdmissionTests
{
	/// <summary>
	/// Entry points that legitimately do not charge the per-command budget, each with the reason
	/// it is safe. Anything not here must call Dispatch or a Try*/admission gate.
	/// </summary>
	private static readonly IReadOnlyDictionary<string, string> ReviewedUnchargedEntryPoints =
		new Dictionary<string, string>( StringComparer.Ordinal )
		{
			["ReportClientBootstrapDiagnostic"] =
				"Charged against the per-connection bootstrap-diagnostic controller, which caps total attempts.",
			["RequestEstablishSession"] =
				"Charged against the per-connection handshake controller; it runs before a command scope exists so it has no request ID."
		};

	[TestMethod]
	public void EveryHostRpcEntryPointIsChargedOrReviewed()
	{
		var unmetered = new List<string>();
		foreach ( var file in HostRpcSourceFiles() )
		{
			var root = CSharpSyntaxTree.ParseText( File.ReadAllText( file ) ).GetRoot();
			foreach ( var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>() )
			{
				if ( !HasHostRpcAttribute( method ) ) continue;
				var name = method.Identifier.ValueText;
				if ( ReviewedUnchargedEntryPoints.ContainsKey( name ) ) continue;
				if ( ChargesAdmission( method ) ) continue;
				unmetered.Add( $"{Path.GetFileName( file )}:{name}" );
			}
		}

		Assert.IsEmpty( unmetered.OrderBy( value => value, StringComparer.Ordinal ).ToArray(),
			"These [Rpc.Host] entry points do no admission charging and are not on the reviewed " +
			$"list: {string.Join( ", ", unmetered )}" );
	}

	[TestMethod]
	public void TheReviewedListDoesNotOutliveTheEntryPointsItExcuses()
	{
		var declared = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var file in HostRpcSourceFiles() )
		{
			var root = CSharpSyntaxTree.ParseText( File.ReadAllText( file ) ).GetRoot();
			foreach ( var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>() )
				if ( HasHostRpcAttribute( method ) ) declared.Add( method.Identifier.ValueText );
		}

		var stale = ReviewedUnchargedEntryPoints.Keys
			.Where( name => !declared.Contains( name ) )
			.OrderBy( name => name, StringComparer.Ordinal )
			.ToArray();
		Assert.IsEmpty( stale,
			$"Reviewed entries name entry points that no longer exist: {string.Join( ", ", stale )}" );
	}

	[TestMethod]
	public void TheGuardSeesTheEntryPointsItIsSupposedToPolice()
	{
		// A guard that silently matches nothing passes forever. Pin that the parse actually finds
		// the surface, so a rename or a move fails here rather than vacating the check.
		var count = 0;
		foreach ( var file in HostRpcSourceFiles() )
		{
			var root = CSharpSyntaxTree.ParseText( File.ReadAllText( file ) ).GetRoot();
			count += root.DescendantNodes().OfType<MethodDeclarationSyntax>().Count( HasHostRpcAttribute );
		}

		Assert.IsGreaterThan( 10, count,
			"The [Rpc.Host] surface should be more than a handful of methods; the parse likely broke." );
	}

	private static bool HasHostRpcAttribute( MethodDeclarationSyntax method ) =>
		method.AttributeLists
			.SelectMany( list => list.Attributes )
			.Any( attribute => attribute.Name.ToString() is "Rpc.Host" or "Host" );

	/// <summary>
	/// Matches real invocations rather than source text. A substring scan over the body reads interior
	/// trivia too, so a comment mentioning the admission call was enough to satisfy the guard — which
	/// made a check on unmetered RPC entry points satisfiable by writing a comment.
	/// </summary>
	private static bool ChargesAdmission( MethodDeclarationSyntax method )
	{
		SyntaxNode? body = method.Body;
		body ??= method.ExpressionBody;
		if ( body is null ) return false;

		return body.DescendantNodes()
			.OfType<InvocationExpressionSyntax>()
			.Select( invocation => invocation.Expression switch
			{
				MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
				IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
				GenericNameSyntax generic => generic.Identifier.ValueText,
				_ => string.Empty
			} )
			.Any( name => name is "Dispatch" or "TryBeginCommand" );
	}

	private static string[] HostRpcSourceFiles()
	{
		var runtime = Path.Combine( RepositoryRoot(), "Code", "V2", "Runtime" );
		var files = Directory.GetFiles( runtime, "*.cs", SearchOption.AllDirectories );
		Assert.IsNotEmpty( files, $"No runtime sources found under '{runtime}'." );
		return files;
	}

	private static string RepositoryRoot()
	{
		var directory = new DirectoryInfo( AppContext.BaseDirectory );
		while ( directory is not null && !File.Exists( Path.Combine( directory.FullName, "hexagon.sbproj" ) ) )
			directory = directory.Parent;
		Assert.IsNotNull( directory, "Could not locate the Hexagon repository root." );
		return directory!.FullName;
	}
}