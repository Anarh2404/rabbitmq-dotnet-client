// This source code is dual-licensed under the Apache License, version
// 2.0, and the Mozilla Public License, version 1.1.
//
// The APL v2.0:
//
//---------------------------------------------------------------------------
//   Copyright (c) 2007-2020 VMware, Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       https://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//---------------------------------------------------------------------------
//
// The MPL v1.1:
//
//---------------------------------------------------------------------------
//  The contents of this file are subject to the Mozilla Public License
//  Version 1.1 (the "License"); you may not use this file except in
//  compliance with the License. You may obtain a copy of the License
//  at https://www.mozilla.org/MPL/
//
//  Software distributed under the License is distributed on an "AS IS"
//  basis, WITHOUT WARRANTY OF ANY KIND, either express or implied. See
//  the License for the specific language governing rights and
//  limitations under the License.
//
//  The Original Code is RabbitMQ.
//
//  The Initial Developer of the Original Code is Pivotal Software, Inc.
//  Copyright (c) 2007-2020 VMware, Inc.  All rights reserved.
//---------------------------------------------------------------------------

using System;
using RabbitMQ.Client.Framing.Impl;
using RabbitMQ.Util;

namespace RabbitMQ.Client.Impl
{
    public enum AssemblyState
    {
        ExpectingMethod,
        ExpectingContentHeader,
        ExpectingContentBody,
        Complete
    }

    public class CommandAssembler
    {
        private const int MaxArrayOfBytesSize = 2_147_483_591;

        private MethodBase m_method;
        private ContentHeaderBase m_header;
        private byte[] m_body;
        private ProtocolBase m_protocol;
        private int m_remainingBodyBytes;
        private AssemblyState m_state;

        private Span<byte> UnwritedSpan => new Span<byte>(m_body, m_body.Length - m_remainingBodyBytes, m_remainingBodyBytes);

        public CommandAssembler(ProtocolBase protocol)
        {
            m_protocol = protocol;
            Reset();
        }

        public Command HandleFrame(InboundFrame f)
        {
            switch (m_state)
            {
                case AssemblyState.ExpectingMethod:
                    {
                        if (!f.IsMethod())
                        {
                            throw new UnexpectedFrameException(f);
                        }
                        BinaryBufferReader reader = f.GetReader();
                        m_method = m_protocol.DecodeMethodFrom(reader);
                        m_state = m_method.HasContent
                            ? AssemblyState.ExpectingContentHeader
                            : AssemblyState.Complete;
                        return CompletedCommand();
                    }
                case AssemblyState.ExpectingContentHeader:
                    {
                        if (!f.IsHeader())
                        {
                            throw new UnexpectedFrameException(f);
                        }
                        BinaryBufferReader reader = f.GetReader();
                        m_header = m_protocol.DecodeContentHeaderFrom(reader);
                        ulong totalBodyBytes = m_header.ReadFrom(reader);
                        if (totalBodyBytes > MaxArrayOfBytesSize)
                        {
                            throw new UnexpectedFrameException(f);
                        }
                        m_remainingBodyBytes = (int)totalBodyBytes;
                        m_body = new byte[totalBodyBytes];
                        UpdateContentBodyState();
                        return CompletedCommand();
                    }
                case AssemblyState.ExpectingContentBody:
                    {
                        if (!f.IsBody())
                        {
                            throw new UnexpectedFrameException(f);
                        }
                        if (f.PayloadSize > m_remainingBodyBytes)
                        {
                            throw new MalformedFrameException
                                (string.Format("Overlong content body received - {0} bytes remaining, {1} bytes received",
                                    m_remainingBodyBytes,
                                    f.PayloadSize));
                        }
                        f.PayloadSpan.CopyTo(UnwritedSpan);
                        m_remainingBodyBytes -= f.PayloadSize;
                        UpdateContentBodyState();
                        return CompletedCommand();
                    }
                case AssemblyState.Complete:
                default:
                    return null;
            }
        }

        private Command CompletedCommand()
        {
            if (m_state == AssemblyState.Complete)
            {
                Command result = new Command(m_method, m_header, m_body);
                Reset();
                return result;
            }
            else
            {
                return null;
            }
        }

        private void Reset()
        {
            m_state = AssemblyState.ExpectingMethod;
            m_method = null;
            m_header = null;
            m_remainingBodyBytes = 0;
        }

        private void UpdateContentBodyState()
        {
            m_state = (m_remainingBodyBytes > 0)
                ? AssemblyState.ExpectingContentBody
                : AssemblyState.Complete;
        }
    }
}
