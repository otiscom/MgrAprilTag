fprintf("\n========================================\n");
fprintf(" ESP32 Vision UDP -> UART Pipeline Debug\n");
fprintf("========================================\n");

scenario = get_dbg_value("dbg_scenario");

fprintf("Vision_Test_Mode        - %d\n", scenario);
fprintf("Scenario description    - %s\n", scenario_description(scenario));
fprintf("----------------------------------------\n");

operator_enable_mask  = get_dbg_value("dbg_operator_enable_mask");
fused_available       = get_dbg_value("dbg_fused_available");
operator_stop_request = get_dbg_value("dbg_operator_stop_request");
safe_to_drive         = get_dbg_value("dbg_safe_to_drive");
hard_stop             = get_dbg_value("dbg_hard_stop");
uart_flags            = get_dbg_value("dbg_uart_flags");
fused_source_id       = get_dbg_value("dbg_fused_source_id");
safe_speed_pct        = get_dbg_value("dbg_safe_speed_pct");

fprintf("operator_enable_mask    - %d\n", operator_enable_mask);
fprintf("fused_available         - %d\n", fused_available);
fprintf("operator_stop_request   - %d\n", operator_stop_request);
fprintf("safe_to_drive           - %d\n", safe_to_drive);
fprintf("hard_stop               - %d\n", hard_stop);
fprintf("uart_flags              - %d\n", uart_flags);
fprintf("fused_source_id        - %d\n", fused_source_id);
fprintf("safe_speed_pct         - %d\n", safe_speed_pct);
fprintf("----------------------------------------\n");
fprintf("Expected summary:\n");

switch scenario
    case 0
        fprintf("  Real UDP mode. Without incoming packets expected: hard_stop=1, uart_flags=128.\n");
    case 1
        fprintf("  Source 1: control + pose. Source 2: observer. Expected: drive enabled, uart_flags=31.\n");
    case 2
        fprintf("  Source 1: control only. Source 2: pose only. Expected: drive enabled, fused_source=2, uart_flags=31.\n");
    case 3
        fprintf("  No deadman/move_en. Expected: hard_stop=1, uart_flags=128.\n");
    otherwise
        fprintf("  Unknown scenario.\n");
end

fprintf("========================================\n\n");

function val = get_dbg_value(varName)
    % Czytanie sygnału debug z obiektu SimulationOutput "out"
    % albo bezpośrednio z base workspace, jeśli kiedyś zmienisz ustawienia.

    if evalin("base", "exist('out','var')")
        simOut = evalin("base", "out");

        if isprop(simOut, varName)
            raw = simOut.(varName);
        else
            error("Brak pola '%s' w obiekcie out.", varName);
        end
    else
        if evalin("base", "exist('" + varName + "','var')")
            raw = evalin("base", varName);
        else
            error("Nie znaleziono zmiennej '%s' ani obiektu out.", varName);
        end
    end

    val = extract_last_value(raw);
end

function val = extract_last_value(raw)
    % Obsługa kilku formatów zapisu z To Workspace:
    % - numeric array
    % - timeseries
    % - struct with time
    % - SimulationData.Signal-like object

    if isa(raw, "timeseries")
        data = raw.Data;
        val = double(data(end));

    elseif isnumeric(raw) || islogical(raw)
        val = double(raw(end));

    elseif isstruct(raw)
        if isfield(raw, "signals")
            data = raw.signals.values;
            val = double(data(end));
        elseif isfield(raw, "Data")
            data = raw.Data;
            val = double(data(end));
        else
            error("Nieobsługiwany struct z To Workspace.");
        end

    elseif isprop(raw, "Values")
        data = raw.Values.Data;
        val = double(data(end));

    else
        error("Nieobsługiwany format danych debug.");
    end

    val = uint8(val);
end

function txt = scenario_description(scenario)
    switch scenario
        case 0
            txt = "realne dane z UDP/dekoderów";
        case 1
            txt = "test: source 1 steruje + pozycja, source 2 obserwator";
        case 2
            txt = "test: source 1 tylko deadman, source 2 tylko pozycja";
        case 3
            txt = "test: brak deadmana -> hard stop";
        otherwise
            txt = "nieznany scenariusz";
    end
end