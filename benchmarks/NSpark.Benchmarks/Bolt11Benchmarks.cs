using BenchmarkDotNet.Attributes;
using NSpark.Services;

namespace NSpark.Benchmarks;

/// <summary>
/// Microbenchmarks for the BOLT-11 decoder used on every Lightning send.
/// </summary>
[MemoryDiagnoser]
public class Bolt11Benchmarks
{
    private const string SpecInvoice =
        "lnbc1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpl2pkx2ctnv5sxxmmwwd5kgetjypeh2ursdae8g6twvus8g6rfwvs8qun0dfjkxaq8rkx3yf5tcsyz3d73gafnh3cax9rn449d9p5uxz9ezhhypd0elx87sjle52x86fux2ypatgddc6k63n7erqz25le42c4u4ecky03ylcqca784w";

    private const string AmountInvoice =
        "lnbc2500u1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpuaztrnwngzn3kdzw5hydlzf03qdgm2hdq27cqv3agm2awhz5se903vruatfhq77w3ls4evs3ch9zw97j25emudupq63nyw24cg27h2rspfj9srp";

    [Benchmark]
    public byte[] DecodeNoAmount() => Bolt11Invoice.Decode(SpecInvoice).PaymentHash;

    [Benchmark]
    public ulong? DecodeWithAmount() => Bolt11Invoice.Decode(AmountInvoice).AmountMsat;
}
