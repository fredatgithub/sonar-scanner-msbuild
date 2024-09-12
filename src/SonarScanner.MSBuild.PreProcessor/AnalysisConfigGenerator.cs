﻿/*
 * SonarScanner for .NET
 * Copyright (C) 2016-2024 SonarSource SA
 * mailto: info AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using SonarScanner.MSBuild.Common;

namespace SonarScanner.MSBuild.PreProcessor;

public static class AnalysisConfigGenerator
{
    private const string SonarExclusions = "sonar.exclusions";

    private static readonly HashSet<string> CoveragePropertyNames =
        [
            "sonar.cs.vscoveragexml.reportsPaths",
            "sonar.cs.dotcover.reportsPaths",
            "sonar.cs.opencover.reportsPaths",
        ];

    /// <summary>
    /// Combines the various configuration options into the AnalysisConfig file
    /// used by the build and post-processor. Saves the file and returns the config instance.
    /// </summary>
    /// <param name="localSettings">Processed local settings, including command line arguments supplied the user.</param>
    /// <param name="buildSettings">Build environment settings.</param>
    /// <param name="additionalSettings">Additional settings generated by this Scanner. Can be empty.</param>
    /// <param name="serverProperties">Analysis properties downloaded from the SonarQube server.</param>
    /// <param name="analyzersSettings">Specifies the Roslyn analyzers to use. Can be empty.</param>
    /// <param name="sonarQubeVersion">SonarQube/SonarCloud server version.</param>
    /// <param name="resolvedJavaExePath">Java exe path calculated from IJreResolver.</param>
    public static AnalysisConfig GenerateFile(ProcessedArgs localSettings,
        BuildSettings buildSettings,
        Dictionary<string, string> additionalSettings,
        IDictionary<string, string> serverProperties,
        List<AnalyzerSettings> analyzersSettings,
        string sonarQubeVersion,
        string resolvedJavaExePath)
    {
        _ = localSettings ?? throw new ArgumentNullException(nameof(localSettings));
        _ = buildSettings ?? throw new ArgumentNullException(nameof(buildSettings));
        _ = additionalSettings ?? throw new ArgumentNullException(nameof(additionalSettings));
        _ = serverProperties ?? throw new ArgumentNullException(nameof(serverProperties));
        _ = analyzersSettings ?? throw new ArgumentNullException(nameof(analyzersSettings));
        var config = new AnalysisConfig
        {
            SonarConfigDir = buildSettings.SonarConfigDirectory,
            SonarOutputDir = buildSettings.SonarOutputDirectory,
            SonarBinDir = buildSettings.SonarBinDirectory,
            SonarScannerWorkingDirectory = buildSettings.SonarScannerWorkingDirectory,
            SourcesDirectory = buildSettings.SourcesDirectory,
            JavaExePath = string.IsNullOrWhiteSpace(localSettings.JavaExePath) ? resolvedJavaExePath : localSettings.JavaExePath, // the user-specified JRE overrides the resolved value
            ScanAllAnalysis = localSettings.ScanAllAnalysis,
            HasBeginStepCommandLineCredentials = localSettings.CmdLineProperties.HasProperty(SonarProperties.SonarUserName)
                                                 || localSettings.CmdLineProperties.HasProperty(SonarProperties.SonarToken),
            SonarQubeHostUrl = localSettings.ServerInfo.ServerUrl,
            SonarQubeVersion = sonarQubeVersion,
            SonarProjectKey = localSettings.ProjectKey,
            SonarProjectVersion = localSettings.ProjectVersion,
            SonarProjectName = localSettings.ProjectName,
            ServerSettings = new(),
            LocalSettings = new(),
            AnalyzersSettings = analyzersSettings
        };
        config.SetBuildUri(buildSettings.BuildUri);
        config.SetTfsUri(buildSettings.TfsUri);
        config.SetVsCoverageConverterToolPath(buildSettings.CoverageToolUserSuppliedPath);
        foreach (var item in additionalSettings)
        {
            config.SetConfigValue(item.Key, item.Value);
        }
        foreach (var property in serverProperties.Where(x => !Utilities.IsSecuredServerProperty(x.Key)))
        {
            AddSetting(config.ServerSettings, property.Key, property.Value);
        }
        foreach (var property in localSettings.CmdLineProperties.GetAllProperties()) // Only those from command line
        {
            AddSetting(config.LocalSettings, property.Id, property.Value);
        }
        if (!string.IsNullOrEmpty(localSettings.Organization))
        {
            AddSetting(config.LocalSettings, SonarProperties.Organization, localSettings.Organization);
        }
        if (localSettings.PropertiesFileName is not null)
        {
            config.SetSettingsFilePath(localSettings.PropertiesFileName);
        }

        HandleCoverageExclusions(config, localSettings, serverProperties);
        config.Save(buildSettings.AnalysisConfigFilePath);
        return config;
    }

    // See https://sonarsource.atlassian.net/browse/SCAN4NET-29
    // This method is a hack and should be removed when we properly support excluding coverage files in the scanner-engine (https://sonarsource.atlassian.net/browse/SCANENGINE-18).
    // The idea is that we are manually adding the coverage paths to the exclusions, so that they do not appear on the analysis.
    private static void HandleCoverageExclusions(AnalysisConfig config, ProcessedArgs localSettings, IDictionary<string, string> serverProperties)
    {
        var localProperties = localSettings.AllProperties().ToList();
        var localCoveragePaths = string.Join(",", localProperties.Where(x => CoveragePropertyNames.Contains(x.Id)).Select(x => x.Value));
        var serverCoveragePaths = string.Join(",", serverProperties.Where(x => CoveragePropertyNames.Contains(x.Key)).Select(x => x.Value));
        if (!localSettings.ScanAllAnalysis                                  // If scanAll analysis is disabled, we will not pick up the coverage files anyways
            || localCoveragePaths.Length + serverCoveragePaths.Length == 0) // If there are no coverage files, there is nothing to exclude
        {
            return;
        }
        var localExclusions = localSettings.GetSetting(SonarExclusions, string.Empty);
        var serverExclusions = serverProperties.ContainsKey(SonarExclusions) ? serverProperties[SonarExclusions] : string.Empty;
        AddCoverageExclusions(localCoveragePaths.Length > 0 ? localCoveragePaths : serverCoveragePaths);

        // The server exclusions are not passed to the scanner-CLI. Instead, they are fetched from the server by the scanner-engine.
        // To prevent the coverage files of appearing in the UI, we need to override the local exclusions + coverage paths.
        void AddCoverageExclusions(string coveragePaths)
        {
            if (string.IsNullOrEmpty(localExclusions) && string.IsNullOrEmpty(serverExclusions))
            {
                localExclusions = coveragePaths;
            }
            else if (string.IsNullOrEmpty(localExclusions))
            {
                localExclusions = string.Join(",", serverExclusions, coveragePaths);
            }
            else
            {
                localExclusions += "," + coveragePaths;
            }
            // Recreate LocalSettings property
            if (config.LocalSettings.Exists(x => x.Id == SonarExclusions)
                || !string.IsNullOrWhiteSpace(localExclusions))
            {
                config.LocalSettings.RemoveAll(x => x.Id == SonarExclusions);
                AddSetting(config.LocalSettings, SonarExclusions, localExclusions);
            }
        }
    }

    private static void AddSetting(AnalysisProperties properties, string id, string value)
    {
        var property = new Property(id, value);

        // Ensure it isn't possible to write sensitive data to the config file
        if (!property.ContainsSensitiveData())
        {
            properties.Add(new(id, value));
        }
    }
}
