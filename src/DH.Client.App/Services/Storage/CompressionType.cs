namespace DH.Client.App.Services.Storage
{
    /// <summary>
    /// 数据压缩算法类型
    /// </summary>
    public enum CompressionType
    {
        /// <summary>
        /// 不压缩
        /// </summary>
        None = 0,
        
        /// <summary>
        /// LZ4 压缩 - 极快速度，中等压缩率
        /// </summary>
        LZ4 = 1,
        
        /// <summary>
        /// Zstd 压缩 - 高速度，高压缩率
        /// </summary>
        Zstd = 2,
        
        /// <summary>
        /// Brotli 压缩 - 中等速度，最高压缩率
        /// </summary>
        Brotli = 3,

        /// <summary>
        /// Snappy 压缩 - Google 出品，极快速度，适合实时场景
        /// </summary>
        Snappy = 4,

        /// <summary>
        /// Zlib 压缩 - 经典通用压缩，兼容性好
        /// </summary>
        Zlib = 5,

        /// <summary>
        /// LZ4_HC 压缩 - LZ4 高压缩模式，速度稍慢但压缩率更高
        /// </summary>
        LZ4_HC = 6,

        /// <summary>
        /// BZip2 压缩 - 高压缩率，适合归档存储
        /// </summary>
        BZip2 = 7
    }
}
