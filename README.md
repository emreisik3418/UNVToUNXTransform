# SAP BO UNV to UNX Data Source Migration Tool

This console application assists in migrating SAP BusinessObjects data sources from UNV (Universe) to UNX format.

## Overview

The tool performs the following steps:
1.  **Authenticates** with SAP BO 4.3 RESTful API.
2.  **Reads** a provided Excel mapping file (`DataSourceMapping.xlsx`) to get the "Source UNV ID" to "Target UNX ID" relations.
3.  **Lists** all Web Intelligence documents from the server.
4.  **Matches** the documents' data sources against the loaded mappings.
5.  **Reports** which documents will be updated based on the matching IDs.

## Prerequisites
- .NET 8.0 SDK or later
- SAP BusinessObjects 4.3 RESTful Web Services access
- Valid SAP BO credentials

## Setup

1. **Configure Settings**:
   Open `appsettings.json` and update the `SapBo` section with your server details:
   ```json
   "SapBo": {
     "BaseUrl": "http://your-server:6405/biprws/",
     "UserName": "Administrator",
     "Password": "Password123",
     "AuthType": "secEnterprise",
     "MaxParallelism": 10,
     "BatchSize": 100,
     "RequestTimeoutSeconds": 100
   }
   ```
   - `MaxParallelism`: how many `dataproviders` lookups run concurrently while scanning documents. Lower it if the CMS/APS servers are struggling under load; raise it (e.g. 20-25) if the server has headroom and scanning feels slow.
   - `BatchSize`: how many documents are grouped together per progress-reporting pass.
   - `RequestTimeoutSeconds`: per-request HTTP timeout. Raise this if large documents or a slow server cause request timeouts.

   The session token is now refreshed **reactively**: the tool only re-logs on when the server actually responds with HTTP 401/403 (expired/invalid token), instead of unconditionally logging off and back on every batch. This removes a significant, unnecessary source of latency on large document sets.

2. **Prepare Mapping File**:
   Ensure a file named `DataSourceMapping.xlsx` is present in the application directory (or configured output directory).
   It must contain the following headers (case-insensitive):
   - `Source UNV ID`
   - `Target UNX ID`

3. **Build the Application**:
   ```bash
   dotnet build
   ```

## Usage

Run the application using:
```bash
dotnet run
```

The application will output the analysis of which documents match your provided mappings.

## Troubleshooting: WSR error codes

These come from the SAP BO RESTful Web Services (Semantic Layer / "raylight") API, not from this tool's own logic. The tool now logs the exact HTTP status and response body from SAP when a call fails, so check the console/log file for the specific message text.

- **WSR 00101** - "The modification operation of the source failed. Please check the relevance of mappings and try again." This is SAP rejecting the auto-generated mapping plan returned by the `dataproviders/mappings` GET call before it's re-applied via POST - typically caused by a data provider that can't be cleanly auto-mapped (free-hand SQL, prompts with a list of values, or objects that don't exist in the target UNX). The tool now retries a failed group of data providers **one at a time** so a single bad data provider doesn't block the others in the same document, and the log clearly names the data provider ID that failed - use that to inspect the mapping in the Information Design Tool / CMC.
- **WSR 00440**: not documented in SAP's public KB at the time of writing. Check the response body the tool now logs alongside the status code for the SAP-provided message text, and correlate with the timing - if it consistently appears after a long idle period or deep into a long run, it likely indicates the session token had expired; the reactive re-logon added in this update should resolve that class of failure.

If either error appears on documents that are known to be valid, re-run just those documents after checking their data source manually in the CMC/IDT - some universes need the LOV/prompt objects to already exist in the target UNX before an automatic mapping can succeed.
