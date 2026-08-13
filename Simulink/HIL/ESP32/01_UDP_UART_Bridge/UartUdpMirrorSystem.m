classdef UartUdpMirrorSystem < matlab.System ...
        & coder.ExternalDependency
    %#codegen

    properties (Nontunable)
        % Adres IPv4 komputera z loggerem.
        % Zmień na rzeczywisty adres PC z ipconfig.
        PcIp0 (1,1) uint8 = uint8(255)
        PcIp1 (1,1) uint8 = uint8(255)
        PcIp2 (1,1) uint8 = uint8(255)
        PcIp3 (1,1) uint8 = uint8(255)

        LocalPort  (1,1) uint16 = uint16(5010)
        RemotePort (1,1) uint16 = uint16(5010)

        % 0.02  -> 50 Hz
        % 0.025 -> 40 Hz
        % 0.005 -> 200 Hz
        SampleTime_s (1,1) double = 0.02
    end

    methods (Static)
        function name = getDescriptiveName()
            name = 'ATU1 UDP Mirror';
        end

        function tf = isSupportedContext(context)
            tf = context.isCodeGenTarget('rtw');
        end

        function updateBuildInfo(buildInfo, context)
            if context.isCodeGenTarget('rtw')
                rootDir = fileparts(mfilename('fullpath'));

                buildInfo.addIncludePaths( ...
                    fullfile(rootDir, 'include'));

                buildInfo.addSourcePaths( ...
                    fullfile(rootDir, 'src'));

                buildInfo.addSourceFiles( ...
                    'UartUdpMirror.cpp');
            end
        end
    end

    methods (Access = protected)

        function setupImpl(obj)
            if coder.target('Rtw')
                coder.cinclude('UartUdpMirror.h');

                coder.ceval( ...
                    'uart_udp_mirror_init', ...
                    obj.PcIp0, ...
                    obj.PcIp1, ...
                    obj.PcIp2, ...
                    obj.PcIp3, ...
                    obj.LocalPort, ...
                    obj.RemotePort);
            end
        end

        function [status, enqueued, sent, dropped, errors] = ...
                stepImpl(~, uart_bytes, send_enable)

            status   = uint8(0);
            enqueued = uint32(0);
            sent     = uint32(0);
            dropped  = uint32(0);
            errors   = uint32(0);

            if coder.target('Rtw')
                coder.ceval( ...
                    'uart_udp_mirror_step', ...
                    coder.rref(uart_bytes), ...
                    uint16(numel(uart_bytes)), ...
                    uint8(send_enable), ...
                    coder.wref(status), ...
                    coder.wref(enqueued), ...
                    coder.wref(sent), ...
                    coder.wref(dropped), ...
                    coder.wref(errors));
            end
        end

        function releaseImpl(~)
            if coder.target('Rtw')
                coder.ceval( ...
                    'uart_udp_mirror_terminate');
            end
        end

        function n = getNumInputsImpl(~)
            n = 2;
        end

        function n = getNumOutputsImpl(~)
            n = 5;
        end

        function [s1, s2, s3, s4, s5] = getOutputSizeImpl(~)
            s1 = [1 1];
            s2 = [1 1];
            s3 = [1 1];
            s4 = [1 1];
            s5 = [1 1];
        end

        function [d1, d2, d3, d4, d5] = ...
                getOutputDataTypeImpl(~)

            d1 = 'uint8';
            d2 = 'uint32';
            d3 = 'uint32';
            d4 = 'uint32';
            d5 = 'uint32';
        end

        function [c1, c2, c3, c4, c5] = ...
                isOutputComplexImpl(~)

            c1 = false;
            c2 = false;
            c3 = false;
            c4 = false;
            c5 = false;
        end

        function [f1, f2, f3, f4, f5] = ...
                isOutputFixedSizeImpl(~)

            f1 = true;
            f2 = true;
            f3 = true;
            f4 = true;
            f5 = true;
        end

       function flag = isInputSizeMutableImpl(~, ~)
              % Oba wejścia mają stały rozmiar:
             % 1: uart_bytes [38x1]
             % 2: send_enable [1x1]
            flag = false;
        end
 
        function sts = getSampleTimeImpl(obj)
            sts = createSampleTime( ...
            obj, ...
            'Type', 'Discrete', ...
            'SampleTime', obj.SampleTime_s);
        end

        function icon = getIconImpl(~)
            icon = sprintf( ...
                'ATU1 UDP Mirror\\nport 5010');
        end

        function [name1, name2] = getInputNamesImpl(~)
            name1 = 'uart_bytes';
            name2 = 'send_enable';
        end

        function [n1, n2, n3, n4, n5] = ...
                getOutputNamesImpl(~)

            n1 = 'status';
            n2 = 'enqueued';
            n3 = 'sent';
            n4 = 'dropped';
            n5 = 'errors';
        end
    end
end