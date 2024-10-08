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
        foreach (var property in GetCommandLineProperties(localSettings))    // Only those from command line
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
        config.Save(buildSettings.AnalysisConfigFilePath);
        return config;
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

    // See https://sonarsource.atlassian.net/browse/SCAN4NET-29
    // This method is a hack and should be removed when we properly support excluding coverage files in the scanner-engine.
    // Instead, it should be replaced at the call site by "localSettings.CmdLineProperties.GetAllProperties()".
    // The idea is that we are manually adding the coverage paths to the exclusions, so that they do not appear on the analysis.
    private static List<Property> GetCommandLineProperties(ProcessedArgs localSettings)
    {
        HashSet<string> coveragePropertyNames =
        [
            "sonar.cs.vscoveragexml.reportsPaths",
            "sonar.cs.dotcover.reportsPaths",
            "sonar.cs.opencover.reportsPaths",
        ];
        var allProperties = localSettings.CmdLineProperties.GetAllProperties().ToList();
        var coveragePaths = string.Join(",", allProperties.Where(x => coveragePropertyNames.Contains(x.Id)).Select(x => x.Value));
        if (!localSettings.ScanAllAnalysis      // if scanAll analysis is disabled, we will not pick up the coverage files anyways
            || coveragePaths.Length == 0)       // if there are no coverage files, there is nothing to exclude
        {
            return allProperties;
        }

        if (allProperties.Find(x => x.Id == "sonar.exclusions") is { } exclusionsProperty)
        {
            exclusionsProperty.Value = exclusionsProperty.Value + "," + coveragePaths;
        }
        else
        {
            allProperties.Add(new("sonar.exclusions", coveragePaths));
        }

        return allProperties;
    }
}
