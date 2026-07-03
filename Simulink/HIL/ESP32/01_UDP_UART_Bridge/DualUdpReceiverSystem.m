classdef DualUdpReceiverSystem < matlab.System ...
        & coder.ExternalDependency
% DualUdpReceiverSystem
%
% Odbiera dwa niezależne strumienie UDP na ESP32:
%   Port A = 5005
%   Port B = 5006
%
% W zwykłej symulacji MATLAB/Simulink blok zwraca zera.
% Kod WiFiUDP jest wykonywany tylko w kodzie wygenerowanym.
%
%#codegen

    properties (Nontunable)
        PortA = uint16(5005);
        PortB = uint16(5006);

        % Musi odpowiadać wymiarowi wejść obecnych dekoderów ATB1.
        MaxPacketSize = uint16(64);

        SampleTime = 0.01;
    end


    methods
        function obj = DualUdpReceiverSystem(varargin)
            setProperties(obj, nargin, varargin{:});
        end
    end


    methods (Access = protected)

        function setupImpl(obj)
            if coder.target('Rtw')
                coder.cinclude('DualUdpReceiver.h');

                coder.ceval( ...
                    'dual_udp_init', ...
                    obj.PortA, ...
                    obj.PortB, ...
                    obj.MaxPacketSize);
            end
        end


        function [dataA, lenA, dataB, lenB, status, rxA, rxB] = ...
                stepImpl(obj)

            packetSize = double(obj.MaxPacketSize);

            dataA = zeros(packetSize, 1, 'uint8');
            dataB = zeros(packetSize, 1, 'uint8');

            lenA = uint16(0);
            lenB = uint16(0);

            status = uint8(0);

            rxA = uint32(0);
            rxB = uint32(0);

            if coder.target('Rtw')
                coder.cinclude('DualUdpReceiver.h');

                coder.ceval( ...
                    'dual_udp_step', ...
                    coder.wref(dataA), ...
                    coder.wref(lenA), ...
                    coder.wref(dataB), ...
                    coder.wref(lenB), ...
                    coder.wref(status), ...
                    coder.wref(rxA), ...
                    coder.wref(rxB));
            end
        end


        function releaseImpl(~)
            if coder.target('Rtw')
                coder.cinclude('DualUdpReceiver.h');
                coder.ceval('dual_udp_terminate');
            end
        end


        function sampleTime = getSampleTimeImpl(obj)
            sampleTime = createSampleTime( ...
                obj, ...
                'Type', 'Discrete', ...
                'SampleTime', obj.SampleTime);
        end


        function num = getNumInputsImpl(~)
            num = 0;
        end


        function num = getNumOutputsImpl(~)
            num = 7;
        end


        function varargout = getOutputSizeImpl(obj)
            packetSize = double(obj.MaxPacketSize);

            varargout{1} = [packetSize, 1]; % dataA
            varargout{2} = [1, 1];          % lenA
            varargout{3} = [packetSize, 1]; % dataB
            varargout{4} = [1, 1];          % lenB
            varargout{5} = [1, 1];          % status
            varargout{6} = [1, 1];          % rxA
            varargout{7} = [1, 1];          % rxB
        end


        function varargout = getOutputDataTypeImpl(~)
            varargout{1} = 'uint8';
            varargout{2} = 'uint16';
            varargout{3} = 'uint8';
            varargout{4} = 'uint16';
            varargout{5} = 'uint8';
            varargout{6} = 'uint32';
            varargout{7} = 'uint32';
        end


        function varargout = isOutputComplexImpl(~)
            for index = 1:7
                varargout{index} = false;
            end
        end


        function varargout = isOutputFixedSizeImpl(~)
            for index = 1:7
                varargout{index} = true;
            end
        end


        function icon = getIconImpl(~)
            icon = sprintf('Dual UDP RX\\n5005 / 5006');
        end


        function [name1, name2, name3, name4, name5, name6, name7] = ...
                getOutputNamesImpl(~)

            name1 = 'data_A';
            name2 = 'len_A';
            name3 = 'data_B';
            name4 = 'len_B';
            name5 = 'status';
            name6 = 'rx_count_A';
            name7 = 'rx_count_B';
        end
    end


    methods (Static)

        function name = getDescriptiveName()
            name = 'ESP32 Dual UDP Receiver';
        end


        function supported = isSupportedContext(context)
            supported = context.isCodeGenTarget('rtw');
        end


        function updateBuildInfo(buildInfo, context)
            if context.isCodeGenTarget('rtw')

                rootDir = fileparts(mfilename('fullpath'));
                sourceDir = fullfile(rootDir, 'src');
                includeDir = fullfile(rootDir, 'include');

                addIncludePaths(buildInfo, includeDir);

                addSourceFiles( ...
                    buildInfo, ...
                    'DualUdpReceiver.cpp', ...
                    sourceDir);
            end
        end
    end
end