//version 250831
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KiwoomAutoTRD
{
    public class StockInfo
    {
        public string Code { get; set; }
        public int Quantity { get; set; }
        public int BuyPrice { get; set; }
        public bool IsTrading { get; set; }
    }
}