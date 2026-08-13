%% Scopes_Data_set_saving_GROUPED_V4_DARK.m
% Grupowany eksport danych z NAJNOWSZEGO runu SDI.
%
% Poprawka względem poprzedniej wersji:
% - folder Control nie szuka już nieistniejących nazw typu src1_valid,
%   tylko bierze kanały z Mux_Source_Status / Mux_Fusion_Status / Mux_Safety_Status,
% - safety w X/Y/Yaw bierze fallback z Mux_Safety_Status,
% - UART ATU1 bierze fallback z MATLAB Function5:1 ... MATLAB Function5:6,
% - każdy folder Scope dostaje:
%       csv/*.csv                  osobne sygnały
%       <scope>_scope.png          jeden zbiorczy obraz jak Scope
%       <scope>_manifest.csv       co zapisano
%       <scope>_missing.csv        czego nie znaleziono
%       <scope>_signals.mat        dane do późniejszego odtworzenia
%
% Workflow:
%   1. Zrób test / Monitor & Tune.
%   2. Kliknij Stop.
%   3. Uruchom:
%        Scopes_Data_set_saving_GROUPED_V4
%
% Przy kolejnej repetycji zmieniasz tylko REP.

%% ===================== KONFIGURACJA =====================

TEST_PREFIX = "Moving_Square";   % np. "Moving_X", "Moving_Y", "Rectangle", "Circle"
REP = 2;                   % np. 1, 2, 3 ...

LOG_ROOT = "C:\Users\mateu\Desktop\Mgr\logs\HIL_Scopes_Grouped\Square";

SAVE_MAT = true;
SAVE_PNG = true;

%% ===================== NAJNOWSZY RUN SDI =====================

runIDs = Simulink.sdi.getAllRunIDs;

if isempty(runIDs)
    error("Brak runów w SDI. Najpierw wykonaj test.");
end

runID = runIDs(end);
runObj = Simulink.sdi.getRun(runID);

REP_TAG = "rep" + string(REP);
RUN_NAME = TEST_PREFIX + "_" + REP_TAG;
TIMESTAMP = string(datetime("now", "Format", "yyyy-MM-dd_HH-mm-ss"));

OUT_DIR = fullfile(LOG_ROOT, char(RUN_NAME + "_" + TIMESTAMP));
mkdirIfMissing(OUT_DIR);

fprintf("\n===============================================\n");
fprintf("HIL SDI Scope/Data Saver GROUPED V4 + DARK PNG\n");
fprintf("RUN_NAME:       %s\n", RUN_NAME);
fprintf("SDI runID:      %d\n", runID);
fprintf("SDI run name:   %s\n", string(runObj.Name));
fprintf("SignalCount:    %d\n", runObj.SignalCount);
fprintf("OUT_DIR:        %s\n", OUT_DIR);
fprintf("===============================================\n\n");

%% ===================== LISTA SYGNAŁÓW Z SDI =====================

allSigNames = strings(double(runObj.SignalCount), 1);
allSigBlockPaths = strings(double(runObj.SignalCount), 1);

for k = 1:double(runObj.SignalCount)
    sig = getSignalByIndex(runObj, k);
    sigName = string(sig.Name);

    if strlength(sigName) == 0
        sigName = "signal_" + string(k);
    end

    allSigNames(k) = sigName;
    allSigBlockPaths(k) = getSignalBlockPathText(sig);
end

inventoryPath = fullfile(OUT_DIR, char(RUN_NAME + "_" + TIMESTAMP + "_sdi_inventory.csv"));
inventoryRows = cell(double(runObj.SignalCount), 3);

for k = 1:double(runObj.SignalCount)
    inventoryRows(k, :) = {double(k), char(allSigNames(k)), char(allSigBlockPaths(k))};
end

writeCellCsv(inventoryPath, [{'signal_index','signal_name','block_path'}; inventoryRows]);

%% ===================== DEFINICJE SCOPE'ÓW =====================
% entry(label, candidates, channels)
% candidates = potencjalne nazwy sygnałów w SDI
% channels   = który kanał wziąć z danego sygnału; 1 dla skalarów

scopeDefs = {};

% ------------------------------------------------------------
% X data Scope
% ------------------------------------------------------------
scopeDefs{end+1} = struct( ...
    'folderName', char(TEST_PREFIX + "_Scope_X_data_" + REP_TAG), ...
    'panelTitles', {{'X signals', 'Safety'}}, ...
    'panelEntries', {{ ...
        { ...
            entry('src1_x', {'src1_x'}, [1]), ...
            entry('src2_x', {'src2_x'}, [1]), ...
            entry('pred1_x', {'pred1_x'}, [1]), ...
            entry('pred2_x', {'pred2_x'}, [1]), ...
            entry('fused_x', {'fused_x'}, [1]), ...
            entry('safe_X', {'safe_X','safe_x'}, [1]) ...
        }, ...
        { ...
            entry('safe_to_drive', {'safe_to_drive','Mux_Safety_Status:1','Mux_Safety_Status','MATLAB Function5:5'}, [1 4 4 1]), ...
            entry('safe_speed_norm', {'safe_speed_norm','Mux_Safety_Status:1','Mux_Safety_Status','Selector8:1','Selector8'}, [1 8 8 1 1]) ...
        } ...
    }});

% ------------------------------------------------------------
% Y data Scope
% ------------------------------------------------------------
scopeDefs{end+1} = struct( ...
    'folderName', char(TEST_PREFIX + "_Scope_Y_data_" + REP_TAG), ...
    'panelTitles', {{'Y signals', 'Safety'}}, ...
    'panelEntries', {{ ...
        { ...
            entry('src1_y', {'src1_y'}, [1]), ...
            entry('src2_y', {'src2_y'}, [1]), ...
            entry('pred1_y', {'pred1_y'}, [1]), ...
            entry('pred2_y', {'pred2_y'}, [1]), ...
            entry('fused_y', {'fused_y'}, [1]), ...
            entry('safe_y', {'safe_y'}, [1]) ...
        }, ...
        { ...
            entry('safe_to_drive', {'safe_to_drive','Mux_Safety_Status:1','Mux_Safety_Status','MATLAB Function5:5'}, [1 4 4 1]), ...
            entry('safe_speed_norm', {'safe_speed_norm','Mux_Safety_Status:1','Mux_Safety_Status','Selector8:1','Selector8'}, [1 8 8 1 1]) ...
        } ...
    }});

% ------------------------------------------------------------
% Yaw Scope
% ------------------------------------------------------------
scopeDefs{end+1} = struct( ...
    'folderName', char(TEST_PREFIX + "_Scope_Yaw_Wrapped_" + REP_TAG), ...
    'panelTitles', {{'Yaw signals', 'Safety'}}, ...
    'panelEntries', {{ ...
        { ...
            entry('src1_yaw', {'src1_yaw_plot','src1_yaw_wrapped','src1_yaw_u'}, [1 1 1]), ...
            entry('src2_yaw', {'src2_yaw_plot','src2_yaw_wrapped','src2_yaw_u'}, [1 1 1]), ...
            entry('pred1_yaw', {'pred1_yaw_plot','pred1_yaw_wrapped','pred1_yaw_u'}, [1 1 1]), ...
            entry('pred2_yaw', {'pred2_yaw_plot','pred2_yaw_wrapped','pred2_yaw_u'}, [1 1 1]), ...
            entry('fused_yaw', {'fused_yaw_plot','fused_yaw_wrapped','fused_yaw_u'}, [1 1 1]), ...
            entry('safe_yaw', {'safe_yaw_plot','safe_yaw_wrapped','safe_yaw_u'}, [1 1 1]) ...
        }, ...
        { ...
            entry('safe_to_drive', {'safe_to_drive','Mux_Safety_Status:1','Mux_Safety_Status','MATLAB Function5:5'}, [1 4 4 1]), ...
            entry('safe_speed_norm', {'safe_speed_norm','Mux_Safety_Status:1','Mux_Safety_Status','Selector8:1','Selector8'}, [1 8 8 1 1]) ...
        } ...
    }});

% ------------------------------------------------------------
% Control Diagnostics Scope
% ------------------------------------------------------------
scopeDefs{end+1} = struct( ...
    'folderName', char(TEST_PREFIX + "_Scope_Control_data_" + REP_TAG), ...
    'panelTitles', {{ ...
        'Source status', ...
        'Fusion status', ...
        'Safety status'}}, ...
    'panelEntries', {{ ...
        { ...
            entry('src1_valid',   {'src1_valid','Mux_Source_Status:1','Mux_Source_Status'}, [1 1 1]), ...
            entry('src1_deadman', {'src1_deadman','Mux_Source_Status:1','Mux_Source_Status'}, [1 2 2]), ...
            entry('src1_move_en', {'src1_move_en','Mux_Source_Status:1','Mux_Source_Status'}, [1 3 3]), ...
            entry('src2_valid',   {'src2_valid','Mux_Source_Status:1','Mux_Source_Status'}, [1 4 4]), ...
            entry('src2_deadman', {'src2_deadman','Mux_Source_Status:1','Mux_Source_Status'}, [1 5 5]), ...
            entry('src2_move_en', {'src2_move_en','Mux_Source_Status:1','Mux_Source_Status'}, [1 6 6]) ...
        }, ...
        { ...
            entry('fused_available',       {'fused_available','Mux_Fusion_Status:1','Mux_Fusion_Status'}, [1 1 1]), ...
            entry('operator_stop_request', {'operator_stop_request','Mux_Fusion_Status:1','Mux_Fusion_Status'}, [1 2 2]), ...
            entry('fused_source_id',       {'fused_source_id','Mux_Fusion_Status:1','Mux_Fusion_Status'}, [1 3 3]), ...
            entry('fusion_mode',           {'fusion_mode','Mux_Fusion_Status:1','Mux_Fusion_Status'}, [1 4 4]), ...
            entry('active_sources_mask',   {'active_sources_mask','Mux_Fusion_Status:1','Mux_Fusion_Status'}, [1 5 5]), ...
            entry('candidate_sources_mask',{'candidate_sources_mask','Mux_Fusion_Status:1','Mux_Fusion_Status'}, [1 6 6]) ...
        }, ...
        { ...
            entry('pose_valid',    {'pose_valid','Mux_Safety_Status:1','Mux_Safety_Status'}, [1 1 1]), ...
            entry('deadman_out',   {'deadman_out','Mux_Safety_Status:1','Mux_Safety_Status'}, [1 2 2]), ...
            entry('move_en_out',   {'move_en_out','Mux_Safety_Status:1','Mux_Safety_Status'}, [1 3 3]), ...
            entry('safe_to_drive', {'safe_to_drive','Mux_Safety_Status:1','Mux_Safety_Status'}, [1 4 4]), ...
            entry('hold_mode',     {'hold_mode','Mux_Safety_Status:1','Mux_Safety_Status'}, [1 5 5]), ...
            entry('soft_decay',    {'soft_decay','Mux_Safety_Status:1','Mux_Safety_Status'}, [1 6 6]), ...
            entry('hard_stop',     {'hard_stop','Mux_Safety_Status:1','Mux_Safety_Status'}, [1 7 7]), ...
            entry('safe_speed_norm', {'safe_speed_norm','Mux_Safety_Status:1','Mux_Safety_Status'}, [1 8 8]) ...
        } ...
    }});

% ------------------------------------------------------------
% UART ATU1 Scope
% ------------------------------------------------------------
scopeDefs{end+1} = struct( ...
    'folderName', char(TEST_PREFIX + "_Scope_UART_ATU1_data_" + REP_TAG), ...
    'panelTitles', {{'ATU1 UART safety flags'}}, ...
    'panelEntries', {{ ...
        { ...
            entry('valid',           {'valid','MATLAB Function5:1'}, [1 1]), ...
            entry('deadman',         {'deadman','MATLAB Function5:2'}, [1 1]), ...
            entry('move_en',         {'move_en','MATLAB Function5:3'}, [1 1]), ...
            entry('fused_available', {'fused_available','MATLAB Function5:4'}, [1 1]), ...
            entry('safe_to_drive',   {'safe_to_drive','MATLAB Function5:5'}, [1 1]), ...
            entry('hard_stop',       {'hard_stop','MATLAB Function5:6'}, [1 1]) ...
        } ...
    }});

%% ===================== EKSPORT GRUPOWANY =====================

masterIndexHeader = {'scope_folder','panel','signal_label','source_signal','source_channel','num_samples','csv_path','conversion_note'};
masterIndexRows = {};

for s = 1:numel(scopeDefs)

    def = scopeDefs{s};
    scopeDir = fullfile(OUT_DIR, def.folderName);
    csvDir = fullfile(scopeDir, "csv");

    mkdirIfMissing(scopeDir);
    mkdirIfMissing(csvDir);

    scopeData = struct();
    exportedRows = {};
    missingRows = {};

    fprintf("Eksport scope: %s\n", def.folderName);

    for p = 1:numel(def.panelEntries)

        entries = def.panelEntries{p};

        for i = 1:numel(entries)

            e = entries{i};

            try
                [time_s, dataCol, sourceName, sourceChannel, conversionNote] = ...
                    extractEntryData(e, runObj, allSigNames);
            catch ME
                missingRows(end+1, 1:3) = {e.label, '', ME.message}; %#ok<SAGROW>
                continue;
            end

            field = matlab.lang.makeValidName(e.label);

            scopeData.(field).label = e.label;
            scopeData.(field).sourceName = sourceName;
            scopeData.(field).sourceChannel = sourceChannel;
            scopeData.(field).time_s = time_s;
            scopeData.(field).data = dataCol;
            scopeData.(field).conversionNote = char(conversionNote);

            csvPath = fullfile(csvDir, sprintf("%02d_%s.csv", numel(exportedRows)+1, field));
            writeSignalCsv(csvPath, time_s, dataCol, string(e.label));

            exportedRows(end+1, 1:8) = { ... %#ok<SAGROW>
                e.label, ...
                def.panelTitles{p}, ...
                sourceName, ...
                sourceChannel, ...
                size(dataCol, 1), ...
                size(dataCol, 2), ...
                csvPath, ...
                char(conversionNote)};

            masterIndexRows(end+1, 1:8) = { ... %#ok<SAGROW>
                def.folderName, ...
                def.panelTitles{p}, ...
                e.label, ...
                sourceName, ...
                sourceChannel, ...
                size(dataCol, 1), ...
                csvPath, ...
                char(conversionNote)};
        end
    end

    manifestPath = fullfile(scopeDir, def.folderName + "_manifest.csv");
    writeCellCsv(manifestPath, [ ...
        {'signal_label','panel','source_signal','source_channel','num_samples','num_channels','csv_path','conversion_note'}; ...
        exportedRows]);

    if ~isempty(missingRows)
        missingPath = fullfile(scopeDir, def.folderName + "_missing.csv");
        writeCellCsv(missingPath, [{'signal_label','source_signal','reason'}; missingRows]);
    else
        missingPath = "";
    end

    if SAVE_MAT && ~isempty(fieldnames(scopeData))
        matPath = fullfile(scopeDir, def.folderName + "_signals.mat");
        save(matPath, '-struct', 'scopeData', '-v7.3');
    else
        matPath = "";
    end

    if SAVE_PNG
        % PNG jasny - obecny wygląd zostaje
        pngPath = fullfile(scopeDir, def.folderName + "_scope.png");
        saveScopeCompositeFigure(def, scopeData, pngPath, "light");

        % PNG ciemny - styl zbliżony do Simulink Scope
        pngDarkPath = fullfile(scopeDir, def.folderName + "_scope_dark.png");
        saveScopeCompositeFigure(def, scopeData, pngDarkPath, "dark");
    else
        pngPath = "";
        pngDarkPath = "";
    end

    summaryPath = fullfile(scopeDir, def.folderName + "_summary.txt");
    fid = fopen(summaryPath, 'w');
    fprintf(fid, "Scope folder: %s\n", def.folderName);
    fprintf(fid, "Run name: %s\n", RUN_NAME);
    fprintf(fid, "Run ID: %d\n", runID);
    fprintf(fid, "SDI run name: %s\n", string(runObj.Name));
    fprintf(fid, "Exported signals: %d\n", size(exportedRows,1));
    fprintf(fid, "Missing signals: %d\n", size(missingRows,1));
    fprintf(fid, "Manifest: %s\n", manifestPath);
    fprintf(fid, "Missing: %s\n", string(missingPath));
    fprintf(fid, "MAT: %s\n", string(matPath));
    fprintf(fid, "PNG light: %s\n", string(pngPath));
    fprintf(fid, "PNG dark: %s\n", string(pngDarkPath));
    fclose(fid);

    fprintf("  exported=%d missing=%d\n", size(exportedRows,1), size(missingRows,1));
    fprintf("  -> zapisano folder: %s\n", scopeDir);
end

%% ===================== MASTER INDEX =====================

masterIndexPath = fullfile(OUT_DIR, RUN_NAME + "_" + TIMESTAMP + "_master_index.csv");
writeCellCsv(masterIndexPath, [masterIndexHeader; masterIndexRows]);

fprintf("\n===============================================\n");
fprintf("GRUPOWANY eksport V4 + DARK PNG zakończony.\n");
fprintf("Folder główny:\n  %s\n", OUT_DIR);
fprintf("Master index:\n  %s\n", masterIndexPath);
fprintf("Inventory:\n  %s\n", inventoryPath);
fprintf("===============================================\n\n");

%% ===================== FUNKCJE LOKALNE =====================

function e = entry(label, candidates, channels)
e = struct();
e.label = char(label);
e.candidates = candidates;
e.channels = channels;
end

function [time_s, dataCol, sourceName, sourceChannel, conversionNote] = extractEntryData(e, runObj, allSigNames)
lastErr = "";

for c = 1:numel(e.candidates)
    candidate = string(e.candidates{c});
    channel = e.channels(c);

    idx = findSignalIndex(candidate, allSigNames);

    if idx <= 0
        lastErr = "not_found: " + candidate;
        continue;
    end

    try
        sig = getSignalByIndex(runObj, idx);
        values = sig.Values;

        [time_s, data2, ~, conversionNote] = valuesToNumericMatrix(values, candidate);

        if size(data2, 2) < channel
            lastErr = sprintf("candidate %s found, but has only %d channels; requested channel %d", ...
                candidate, size(data2, 2), channel);
            continue;
        end

        dataCol = data2(:, channel);
        sourceName = char(candidate);
        sourceChannel = double(channel);
        return;

    catch ME
        lastErr = string(ME.message);
    end
end

error("%s", lastErr);
end

function idx = findSignalIndex(candidate, allSigNames)
idx = 0;

% exact case-insensitive
m = find(strcmpi(allSigNames, candidate), 1, 'first');
if ~isempty(m)
    idx = double(m);
    return;
end

% contains fallback
m = find(contains(lower(allSigNames), lower(candidate)), 1, 'first');
if ~isempty(m)
    idx = double(m);
    return;
end
end

function mkdirIfMissing(pathText)
if ~exist(pathText, "dir")
    mkdir(pathText);
end
end

function blockPathText = getSignalBlockPathText(sig)
blockPathText = "";

try
    bp = sig.BlockPath;
    blockPathText = string(bp);
catch
    blockPathText = "";
end

if strlength(blockPathText) == 0
    try
        blockPathText = string(sig.Model);
    catch
        blockPathText = "";
    end
end
end

function [time_s, data2, channelNames, conversionNote] = valuesToNumericMatrix(values, sigName)
conversionNote = "";

if isa(values, "timeseries")
    time_s = values.Time(:);
    data = values.Data;

    if isduration(time_s)
        time_s = seconds(time_s);
    end

    time_s = double(time_s(:));

    [data2, conversionNote] = dataToMatrix(data, numel(time_s));

    nRows = min(numel(time_s), size(data2, 1));
    if nRows <= 0
        error("Brak próbek.");
    end

    if size(data2, 1) ~= numel(time_s)
        conversionNote = conversionNote + ...
            sprintf(" | row_fix time=%d dataRows=%d used=%d", ...
            numel(time_s), size(data2, 1), nRows);
    end

    time_s = time_s(1:nRows);
    data2 = data2(1:nRows, :);

    data2 = double(data2);
    channelNames = makeChannelNames(sigName, size(data2, 2));
    return;
end

if istimetable(values)
    T = timetable2table(values);

    if height(T) == 0
        error("Pusty timetable.");
    end

    rawTime = T{:,1};

    if isduration(rawTime)
        time_s = seconds(rawTime);
    elseif isdatetime(rawTime)
        time_s = seconds(rawTime - rawTime(1));
    else
        time_s = double(rawTime);
    end

    time_s = time_s(:);

    numericCols = [];
    names = strings(1,0);

    for c = 2:width(T)
        col = T{:,c};

        if isnumeric(col) || islogical(col)
            col2 = double(col);
            col2 = reshape(col2, height(T), []);
            numericCols = [numericCols, col2]; %#ok<AGROW>

            for j = 1:size(col2,2)
                names(end+1) = matlab.lang.makeValidName(string(T.Properties.VariableNames{c}) + "_ch" + string(j)); %#ok<AGROW>
            end
        end
    end

    if isempty(numericCols)
        error("Timetable nie ma kolumn numerycznych.");
    end

    data2 = numericCols;
    channelNames = matlab.lang.makeUniqueStrings(names);
    conversionNote = "timetable numeric columns";
    return;
end

error("Nieobsługiwany typ Values: %s", class(values));
end

function [data2, note] = dataToMatrix(data, nTime)
note = "";

if isempty(data)
    data2 = zeros(nTime, 0);
    note = "empty data";
    return;
end

if iscell(data)
    [data2, note] = cellDataToMatrix(data, nTime);
    return;
end

if ~(isnumeric(data) || islogical(data))
    error("Dane nie są numeric/logical/cell. class=%s", class(data));
end

sz = size(data);
nd = ndims(data);

if nTime == 1
    data2 = reshape(data, 1, []);
    note = sprintf("nTime=1 reshape [%s]", num2str(sz));
    return;
end

if isvector(data) && numel(data) == nTime
    data2 = reshape(data, nTime, 1);
    note = "vector";
    return;
end

if sz(1) == nTime
    data2 = reshape(data, nTime, []);
    note = sprintf("time_dim_first [%s]", num2str(sz));
    return;
end

if sz(end) == nTime
    order = [nd, 1:nd-1];
    dataPerm = permute(data, order);
    data2 = reshape(dataPerm, nTime, []);
    note = sprintf("time_dim_last [%s]", num2str(sz));
    return;
end

candidateDims = find(sz == nTime);

if ~isempty(candidateDims)
    timeDim = candidateDims(1);
    order = [timeDim, setdiff(1:nd, timeDim, "stable")];
    dataPerm = permute(data, order);
    data2 = reshape(dataPerm, nTime, []);
    note = sprintf("time_dim_%d [%s]", timeDim, num2str(sz));
    return;
end

if mod(numel(data), nTime) == 0
    data2 = reshape(data, nTime, []);
    note = sprintf("reshape_numel [%s]", num2str(sz));
    return;
end

flat = data(:);
nRows = min(nTime, numel(flat));
data2 = reshape(flat(1:nRows), nRows, 1);
note = sprintf("fallback_flat_truncated [%s] nTime=%d usedRows=%d", ...
    num2str(sz), nTime, nRows);
end

function [data2, note] = cellDataToMatrix(data, nTime)
flatCells = data(:);
nRows = min(nTime, numel(flatCells));

maxLen = 1;
for i = 1:nRows
    value = flatCells{i};
    if isnumeric(value) || islogical(value)
        maxLen = max(maxLen, numel(value));
    end
end

data2 = NaN(nRows, maxLen);

for i = 1:nRows
    value = flatCells{i};
    if isnumeric(value) || islogical(value)
        row = reshape(value, 1, []);
        data2(i, 1:numel(row)) = row;
    end
end

note = sprintf("cell_data rows=%d cols=%d", nRows, maxLen);
end

function channelNames = makeChannelNames(sigName, nChannels)
safeBase = matlab.lang.makeValidName(sigName);
channelNames = strings(1, nChannels);

if nChannels == 1
    channelNames(1) = safeBase;
else
    for c = 1:nChannels
        channelNames(c) = safeBase + "_ch" + string(c);
    end
end

channelNames = matlab.lang.makeUniqueStrings(channelNames);
end

function writeSignalCsv(csvPath, time_s, data2, channelNames)
fid = fopen(csvPath, 'w');
if fid < 0
    error("Nie mogę otworzyć pliku CSV: %s", csvPath);
end
cleaner = onCleanup(@() fclose(fid));

fprintf(fid, "time_s");
for c = 1:numel(channelNames)
    fprintf(fid, ",%s", sanitizeCsvHeader(channelNames(c)));
end
fprintf(fid, "\n");

nRows = size(data2, 1);
nCols = size(data2, 2);

if numel(time_s) ~= nRows
    nRows = min(numel(time_s), nRows);
    time_s = time_s(1:nRows);
    data2 = data2(1:nRows, :);
end

for r = 1:nRows
    fprintf(fid, "%.9g", time_s(r));
    for c = 1:nCols
        fprintf(fid, ",%.9g", data2(r,c));
    end
    fprintf(fid, "\n");
end
end

function h = sanitizeCsvHeader(s)
h = char(string(s));
h = strrep(h, ",", "_");
h = strrep(h, newline, "_");
h = strrep(h, sprintf("\r"), "_");
end

function writeCellCsv(pathText, cells)
fid = fopen(pathText, "w");
if fid < 0
    error("Nie mogę otworzyć pliku: %s", pathText);
end
cleaner = onCleanup(@() fclose(fid));

[nRows, nCols] = size(cells);

for r = 1:nRows
    for c = 1:nCols
        if c > 1
            fprintf(fid, ",");
        end
        value = cells{r,c};
        fprintf(fid, "%s", csvEscape(value));
    end
    fprintf(fid, "\n");
end
end

function s = csvEscape(value)
if isnumeric(value)
    if isscalar(value)
        s = num2str(value);
    else
        s = mat2str(value);
    end
elseif islogical(value)
    s = num2str(double(value));
elseif isstring(value) || ischar(value)
    s = char(string(value));
else
    try
        s = char(string(value));
    catch
        s = "<unprintable>";
    end
end

s = strrep(s, """", """""");

if contains(s, ",") || contains(s, newline) || contains(s, sprintf("\r"))
    s = ['"', s, '"'];
end
end

function saveScopeCompositeFigure(def, scopeData, pngPath, theme)
if nargin < 4
    theme = "light";
end

theme = lower(string(theme));

switch theme
    case "dark"
        figColor   = [0.06 0.06 0.06];
        axColor    = [0.00 0.00 0.00];
        textColor  = [0.94 0.94 0.94];
        gridColor  = [0.35 0.35 0.35];
        legendBg   = [0.08 0.08 0.08];
        legendEdge = [0.70 0.70 0.70];
    otherwise
        figColor   = [1.00 1.00 1.00];
        axColor    = [1.00 1.00 1.00];
        textColor  = [0.00 0.00 0.00];
        gridColor  = [0.85 0.85 0.85];
        legendBg   = [1.00 1.00 1.00];
        legendEdge = [0.30 0.30 0.30];
end

fig = figure( ...
    'Visible', 'off', ...
    'Color', figColor, ...
    'Position', [100 100 1800 1000]);

tl = tiledlayout(numel(def.panelEntries), 1, ...
    'TileSpacing', 'compact', ...
    'Padding', 'compact');

for p = 1:numel(def.panelEntries)
    ax = nexttile(tl);
    hold(ax, 'on');

    entries = def.panelEntries{p};
    plotted = false;

    set(ax, ...
        'Color', axColor, ...
        'XColor', textColor, ...
        'YColor', textColor, ...
        'GridColor', gridColor, ...
        'MinorGridColor', gridColor);

    xMin = inf;
    xMax = -inf;

    for i = 1:numel(entries)
        e = entries{i};
        field = matlab.lang.makeValidName(e.label);

        if isfield(scopeData, field)
            time_s = scopeData.(field).time_s;
            data = scopeData.(field).data;

            if isempty(time_s) || isempty(data)
                continue;
            end

            y = data(:, 1);

            if isBinarySignal(y)
                stairs(ax, time_s, y, ...
                    'LineWidth', 1.2, ...
                    'DisplayName', e.label);
            else
                plot(ax, time_s, y, ...
                    'LineWidth', 1.2, ...
                    'DisplayName', e.label);
            end

            xMin = min(xMin, min(time_s));
            xMax = max(xMax, max(time_s));

            plotted = true;
        end
    end

    grid(ax, 'on');
    xlabel(ax, 'time [s]', 'Color', textColor);
    ylabel(ax, 'value', 'Color', textColor);

    title(ax, def.panelTitles{p}, ...
        'Interpreter', 'none', ...
        'Color', textColor);

    if plotted
        if isfinite(xMin) && isfinite(xMax) && xMax > xMin
            xlim(ax, [xMin xMax]);
        end

        lgd = legend(ax, ...
            'Interpreter', 'none', ...
            'Location', 'best');

        set(lgd, ...
            'TextColor', textColor, ...
            'Color', legendBg, ...
            'EdgeColor', legendEdge);
    else
        text(ax, 0.5, 0.5, 'No matched SDI signals', ...
            'Units', 'normalized', ...
            'HorizontalAlignment', 'center', ...
            'FontWeight', 'bold', ...
            'Color', textColor);
    end
end

title(tl, def.folderName, ...
    'Interpreter', 'none', ...
    'Color', textColor);

try
    exportgraphics(fig, pngPath, 'Resolution', 150);
catch
    % Fallback dla starszych / kapryśnych konfiguracji MATLAB-a.
    saveas(fig, pngPath);
end

close(fig);
end

function tf = isBinarySignal(y)
y = y(~isnan(y));

if isempty(y)
    tf = false;
    return;
end

u = unique(y);
tf = all(ismember(u, [0 1]));
end

