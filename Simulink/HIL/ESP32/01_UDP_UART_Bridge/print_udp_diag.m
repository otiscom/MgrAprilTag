fprintf("\n========================================\n");
fprintf(" UDP Link Diagnostics\n");
fprintf("========================================\n");

u32 = get_last_dbg("dbg_udp_diag_selected_u32");
u8  = get_last_dbg("dbg_udp_diag_selected_u8");
sumu32 = get_last_dbg("dbg_udp_diag_summary_u32");

fprintf("Selected source diagnostics:\n");
fprintf("rx_ok_total             - %u\n", u32(1));
fprintf("crc_bad_total           - %u\n", u32(2));
fprintf("seq_lost_total          - %u\n", u32(3));
fprintf("duplicate_or_old        - %u\n", u32(4));
fprintf("valid_count             - %u\n", u32(5));
fprintf("deadman_count           - %u\n", u32(6));
fprintf("move_en_count           - %u\n", u32(7));
fprintf("last_seq                - %u\n", u32(8));
fprintf("last_gap_ms             - %u\n", u32(10));
fprintf("max_gap_ms              - %u\n", u32(11));
fprintf("avg_gap_ms              - %u\n", u32(12));
fprintf("loss_permille           - %u\n", u32(13));
fprintf("valid_ratio_percent     - %u\n", u32(14));
fprintf("session_id              - %u\n", u32(15));
fprintf("session_rx_ok           - %u\n", u32(16));
fprintf("session_lost            - %u\n", u32(17));
fprintf("session_max_gap_ms      - %u\n", u32(18));
fprintf("age_ms                  - %u\n", u32(19));
fprintf("last_unity_t_ms         - %u\n", u32(20));

fprintf("\nSelected source flags:\n");
fprintf("source_seen             - %u\n", u8(1));
fprintf("fresh_300ms             - %u\n", u8(2));
fprintf("in_session              - %u\n", u8(3));
fprintf("last_valid              - %u\n", u8(4));
fprintf("last_deadman            - %u\n", u8(5));
fprintf("last_move_en            - %u\n", u8(6));
fprintf("last_port_id            - %u\n", u8(7));
fprintf("last_error_code         - %u\n", u8(8));

fprintf("\nSummary:\n");
fprintf("total_rx_ok             - %u\n", sumu32(1));
fprintf("total_lost              - %u\n", sumu32(2));
fprintf("total_crc_bad           - %u\n", sumu32(3));
fprintf("total_duplicate_or_old  - %u\n", sumu32(4));
fprintf("sources_seen_mask       - %u\n", sumu32(5));
fprintf("fresh_sources_mask      - %u\n", sumu32(6));
fprintf("max_gap_all_ms          - %u\n", sumu32(7));
fprintf("worst_age_ms            - %u\n", sumu32(8));
fprintf("total_loss_permille     - %u\n", sumu32(9));
fprintf("fresh_sources_count     - %u\n", sumu32(10));
fprintf("total_sessions          - %u\n", sumu32(11));

fprintf("========================================\n\n");

function val = get_last_dbg(varName)
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

    if isa(raw, "timeseries")
        data = raw.Data;
        val = squeeze(data(end, :));
        if numel(val) == 1
            val = raw.Data(end);
        end
    elseif isnumeric(raw)
        val = raw;
    elseif isprop(raw, "Values")
        data = raw.Values.Data;
        val = squeeze(data(end, :));
    else
        error("Nieobsługiwany format danych debug dla '%s'.", varName);
    end

    val = uint32(val(:));
end