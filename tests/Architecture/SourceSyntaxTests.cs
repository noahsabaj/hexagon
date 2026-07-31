#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Hexagon.V2.Tests.Architecture;

/// <summary>
/// Parse-only syntax gate over every product source file — including the engine-facing
/// Runtime/Infrastructure files excluded from test compilation — under both the neutral
/// symbol set and the dedicated-server SERVER set. An unparseable engine-facing file can
/// no longer merge green; semantic compilation of those files remains the generated
/// s&amp;box build's job (verify.ps1).
/// </summary>
[TestClass]
public sealed class SourceSyntaxTests
{
	private static readonly string[][] SymbolSets = [[], ["SERVER"]];

	[TestMethod]
	public void EveryProductSourceParsesUnderNeutralAndServerSymbolSets()
	{
		var failures = new List<string>();
		var files = ProductSourceFiles( Path.Combine( ProductRoot(), "Code" ) ).ToArray();
		Assert.IsNotEmpty( files, "The syntax gate found no product sources to parse." );
		foreach ( var symbols in SymbolSets )
		{
			var options = new CSharpParseOptions( LanguageVersion.Preview, preprocessorSymbols: symbols );
			foreach ( var file in files )
			{
				var tree = CSharpSyntaxTree.ParseText( File.ReadAllText( file ), options, file );
				failures.AddRange( tree.GetDiagnostics()
					.Where( diagnostic => diagnostic.Severity == DiagnosticSeverity.Error )
					.Select( diagnostic => $"[{string.Join( ',', symbols )}] {diagnostic}" ) );
			}
		}

		Assert.IsEmpty( failures, string.Join( Environment.NewLine, failures ) );
	}

	internal static IEnumerable<string> ProductSourceFiles( string codeRoot ) =>
		Directory.EnumerateFiles( codeRoot, "*.cs", SearchOption.AllDirectories )
			.Where( file => !file.Split( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar )
				.Any( segment => segment is "obj" or "bin" ) );

	private static string ProductRoot()
	{
		var current = new DirectoryInfo( AppContext.BaseDirectory );
		while ( current is not null )
		{
			if ( Directory.Exists( Path.Combine( current.FullName, "Code", "V2" ) ) )
				return current.FullName;
			current = current.Parent;
		}

		throw new DirectoryNotFoundException( "Could not locate the Hexagon product root from the test output directory." );
	}
}
