using PDTools.Utils;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GTPSPVolTools.Packing;

public abstract class PageHolderBase
{
    public abstract void Write(ref BitStream stream);
}
