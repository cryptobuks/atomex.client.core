﻿using Atomex.Common.Memory;
using Atomex.Wallets.Abstract;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Atomex.Wallets.Tezos.Fa12
{
    public class Fa12Config : TezosConfig
    {
        public string TokenContract { get; set; }
    }
}