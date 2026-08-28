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
     "RequestTimeoutSeconds": 100,
     "CmsQueryPageSize": 200
   }
   ```
   - `MaxParallelism`: how many `dataproviders` lookups run concurrently while scanning documents. Lower it if the CMS/APS servers are struggling under load; raise it (e.g. 20-25) if the server has headroom and scanning feels slow.
   - `BatchSize`: how many documents are grouped together per progress-reporting pass.
   - `RequestTimeoutSeconds`: per-request HTTP timeout. Raise this if large documents or a slow server cause request timeouts.
   - `CmsQueryPageSize`: page size used when listing documents via `/v1/cmsquery`. SAP BO defaults to **50 results per call** and silently truncates anything beyond that if the client doesn't page through results - the tool now pages automatically using this size until a short page is returned.

   The session token is now refreshed **reactively**: the tool only re-logs on when the server actually responds with HTTP 401/403 (expired/invalid token), instead of unconditionally logging off and back on every batch. This removes a significant, unnecessary source of latency on large document sets.

   Optionally, restrict the scan to one CMS folder (and everything under it) via the `Application` section:
   ```json
   "Application": {
     "OutputDirectory": ".",
     "MappingFileName": "DataSourceMapping.xlsx",
     "SourceFolderId": 12345
   }
   ```
   `SourceFolderId` is the folder's `SI_ID` (find it in the CMC, or via the folder's URL/properties). It's translated into an `SI_ANCESTOR = <id>` condition in the CMS query, which matches documents anywhere under that folder, not just direct children. Leave it `null` to scan the whole repository.

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

### "Rule not respected(Argument, "targetDatasourceId" must not be null)"

This means the `Target UNX ID` value sent to SAP for the `targetDataSourceId` parameter didn't reach the server intact. Two causes are now handled:

1. **Missing mapping**: the matched Excel row had an empty `Target UNX ID` cell. The tool now checks for this explicitly and logs `Target UNX ID is empty for Document ... Check the Excel mapping file...` instead of sending a broken request - fix the Excel row for that Source UNV ID.
2. **Unescaped special characters**: SAP CUIDs (the long IDs used for `Target UNX ID`) commonly contain `+`, `/`, and `=` characters, which are not safe inside a raw URL query string - they can be silently dropped or misinterpreted by the server's query parser. The tool now URL-encodes both `originDataProviderIds` and `targetDataSourceId` before building the request. If you were hitting this error with a Target UNX ID that *did* have a value in Excel, this was almost certainly the cause.

### WSR 00999 / RWI 00239 - "The maximum connection limit has been reached"

Every call to `raylight/v1/documents/{id}/...` (listing data providers, fetching/applying a mapping plan, saving, copying) opens a Web Intelligence Processing Server session for that document. That session stays open - consuming one of the server's limited connection slots - until it's explicitly released with `DELETE raylight/v1/documents/{id}`. The tool previously never sent that DELETE, so a full repository scan (or a run over many documents) would accumulate open sessions until the server refused new ones with this error.

The tool now closes each document's session as soon as it's done with it: right after reading its data providers during the scan, right after a backup copy is created, and right after a document's data source updates are saved. This close is implemented as a `PUT` with an empty body to `raylight/v1/documents/{id}`, per the WebI RESTful SDK's Life Cycle Management API - **not** `DELETE`, which is not a supported method on this resource and, in an earlier revision of this fix, appears to have been routed to the server's generic delete handler and removed the CMS document instead of just closing its session. If you ran that revision and are now seeing `"The resource of type 'Document' with identifier '...' does not exist"` on documents you know are valid, check the CMC/BI Launch Pad (and the tool's own `_Backup_...` copies, if Phase 1 completed before the failure) to confirm whether those documents were actually deleted.

If you still see the connection-limit error after this fix:
- Check the WebI Processing Server's `Maximum Connections` setting in the CMC (Servers > your WIPS) - it may be set low for your document volume.
- Lower `MaxParallelism` in `appsettings.json` so fewer documents are open at once during the scan.
- Sessions from a *previous* run that crashed before this fix may still be lingering on the server; they'll clear on their own after the WIPS idle timeout, or can be recycled by restarting the WebI Processing Server in the CMC.
