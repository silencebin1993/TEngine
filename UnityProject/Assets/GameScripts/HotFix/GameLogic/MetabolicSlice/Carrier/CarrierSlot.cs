namespace GameLogic.MetabolicSlice.Carrier
{
    /// <summary>Carrier 有序插槽之一。GeneInstanceId 为 null 表示空槽（D6：002 只存身份引用字符串，
    /// 不存活的 IContract/IModule 实例——那是编译时机，留给 003）。</summary>
    public sealed class CarrierSlot
    {
        public int Index { get; }
        public string GeneInstanceId { get; set; }

        public CarrierSlot(int index)
        {
            Index = index;
            GeneInstanceId = null;
        }
    }
}
