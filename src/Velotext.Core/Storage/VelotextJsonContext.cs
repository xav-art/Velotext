// Velotext: source-generated JSON serialization settings for every stored file.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using Velotext.Core.Model;
using Velotext.Core.Settings;
using Velotext.Core.Usage;

namespace Velotext.Core.Storage;

/// <summary>
/// Source-generated JSON metadata for Velotext files. Camel-case names, readable indentation, enums
/// as strings, and strict null handling so a hand-edited or foreign file cannot smuggle nulls in.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    MaxDepth = 96,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PhraseLibrary))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(UsageData))]
[JsonSerializable(typeof(ExchangeDocument))]
public sealed partial class VelotextJsonContext : JsonSerializerContext;
