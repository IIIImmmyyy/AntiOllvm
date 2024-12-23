using System.Reflection.Emit;
using AntiOllvm.analyze;
using AntiOllvm.entity;
using AntiOllvm.Extension;
using AntiOllvm.Helper;
using AntiOllvm.Logging;
using OpCode = AntiOllvm.entity.OpCode;

namespace AntiOllvm.Analyze;

public class Simulation
{
    private readonly List<Block> _blocks;

    private Block _mainDispatcher;
    private RegisterContext _regContext;
    private IAnalyze _analyzer;
    private Instruction _lastCompareIns;

    public IAnalyze Analyzer => _analyzer;
    private string _outJsonPath;
    private IDACFG _idacfg;

    private List<Block> _childDispatcherBlocks = new List<Block>();

    private List<Block> _realBlocks = new List<Block>();

    public Simulation(string json, string outJsonPath)
    {
        _outJsonPath = outJsonPath;
        _idacfg = JsonHelper.Format<IDACFG>(json);
        _blocks = _idacfg.cfg;
        _regContext = new RegisterContext();
        Logger.InfoNewline("Simulation initialized with " + _blocks.Count + " blocks");
    }

    public void SetAnalyze(IAnalyze iAnalyze)
    {
        _analyzer = iAnalyze;
    }

    public void Run()
    {
        //在初始化寄存器时需要先定位主分发器 
        foreach (var block in _blocks)
        {
            if (_analyzer.IsMainDispatcher(block, _regContext, _blocks, this))
            {
                _mainDispatcher = block;
                break;
            }
        }

        if (_mainDispatcher == null)
        {
            throw new Exception(" Main dispatcher not found");
        }

        InitRegister();
    }

    private void LogRegisters()
    {
        _regContext.LogRegisters();
    }

    private void InitRegister()
    {
        foreach (var block in _blocks)
        {
            if (block.Equals(_mainDispatcher))
            {
                Logger.InfoNewline("Main dispatcher found " + block.start_address);
                break;
            }

            foreach (var instruction in block.instructions)
            {
                AssignRegisterByInstruction(instruction);
            }
        }

        _analyzer.InitAfterRegisterAssign(_regContext, _mainDispatcher, _blocks, this);
        LogRegisters();
        AnalyzeInstruction();
    }


    private void ReBuildCFGBlocks()
    {
        //Start in MainDispatcher;
        var nextBlock = RunDispatchBlock(_mainDispatcher);
        if (nextBlock == null)
        {
            throw new Exception("MainDispatch must be has next Block ");
        }


        var block = FindRealBlock(nextBlock);
        Logger.ErrorNewline("=========================================================\n" +
                            "=========================================================");
        Logger.InfoNewline("Start Fix ReadBlock Count is  " + _realBlocks.Count);
        foreach (var realBlock in _realBlocks)
        {
            if (realBlock.isFix)
            {
                continue;
            }

            realBlock.FixMachineCodeNew(_mainDispatcher, this);
        }

        _mainDispatcher.LinkToFirstRealBlock(block.GetStartAddress());

        List<Instruction> fixInstructions = new List<Instruction>();
        FixMainDispatcher(fixInstructions);
        FixChildDispatcher(fixInstructions);
        Logger.InfoNewline("Child Dispatcher Count " + _childDispatcherBlocks.Count
                                                     + "RealBlock Fix Start " + fixInstructions.Count);
        // GetAllFixInstruction(block, ref fixInstructions);

        foreach (var realBlock in _realBlocks)
        {
            foreach (var instruction in realBlock.instructions)
            {
                if (!string.IsNullOrEmpty(instruction.fixmachine_code))
                {
                    if (!fixInstructions.Contains(instruction))
                    {
                        fixInstructions.Add(instruction);
                    }
                }

                if (!string.IsNullOrEmpty(instruction.fixmachine_byteCode))
                {
                    if (!fixInstructions.Contains(instruction))
                    {
                        fixInstructions.Add(instruction);
                    }
                }
            }
        }

        File.WriteAllText(_outJsonPath, JsonHelper.ToString(fixInstructions));

        OutLogger.InfoNewline("All Instruction is Fix Done Count is " + fixInstructions.Count);
        OutLogger.InfoNewline("FixJson OutPath is " + _outJsonPath);
    }

    private void FixMainDispatcher(List<Instruction> fixInstructions)
    {
        foreach (var instruction in _mainDispatcher.instructions)
        {
            fixInstructions.Add(instruction);
        }
    }

    private void FixChildDispatcher(List<Instruction> fixInstructions)
    {
        foreach (var block in _childDispatcherBlocks)
        {
            foreach (var instruction in block.instructions)
            {
                instruction.fixmachine_code = "NOP";
                fixInstructions.Add(instruction);
            }
        }
    }


    private Block FindRealBlock(Block block)
    {
        if (_analyzer.IsMainDispatcher(block, _regContext, _blocks, this))
        {
            var next = RunDispatchBlock(block);
            return FindRealBlock(next);
        }

        if (_analyzer.IsChildDispatcher(block, _mainDispatcher, _regContext))
        {
            Logger.InfoNewline(" is Child Dispatcher " + block.start_address);
            var next = RunDispatchBlock(block);
            if (!_childDispatcherBlocks.Contains(block))
            {
                _childDispatcherBlocks.Add(block);
            }

            return FindRealBlock(next);
        }

        if (_analyzer.IsRealBlock(block, _mainDispatcher, _regContext))
        {
            Logger.WarnNewline("Find Real Block \n" + block);
            block.RealChilds = GetAllChildBlockNew(block);
            if (!_realBlocks.Contains(block))
            {
                _realBlocks.Add(block);
            }

            return block;
        }

        throw new Exception("is unknown block \n" + block);
    }

    private void SyncLogicInstruction(Instruction instruction)
    {
        switch (instruction.Opcode())
        {
            case OpCode.MOV:
            {
                //Assign register
                var left = instruction.Operands()[0];
                var right = instruction.Operands()[1];
                if (left.kind == Arm64OperandKind.Register && right.kind == Arm64OperandKind.Immediate)
                {
                    //Assign immediate value to register
                    var register = GetRegister(left.registerName);
                    var imm = right.immediateValue;
                    register.value = imm;
                    Logger.ErrorNewline($"Update  MOV {left.registerName} = {imm} ({imm:X})");
                }

                break;
            }
            case OpCode.MOVK:
            {
                var dest = instruction.Operands()[0];
                var imm = instruction.Operands()[1].immediateValue;
                var shift = instruction.Operands()[2].shiftType;
                var reg = GetRegister(dest.registerName);
                var v = MathHelper.CalculateMOVK(reg.GetLongValue(), imm, shift, instruction.Operands()[2].shiftValue);
                reg.value = v;
                Logger.InfoNewline($"Update MOVK {dest.registerName} = {imm} ({imm:X})");
                break;
            }
        }
    }

    private void SyncLogicBlock(Block block)
    {
        foreach (var instruction in block.instructions)
        {
            SyncLogicInstruction(instruction);
        }
    }

    private Instruction IsCSELAfterMove(Block block, Instruction CSEL)
    {
        var index = block.FindIndex(CSEL);
        if (index == -1)
        {
            return null;
        }

        var lastIns = block.instructions[^1];
        var link = block.GetLinkedBlocks(this);
        bool IsDispatch = false;
        if (link.Count == 1)
        {
            if (_analyzer.IsMainDispatcher(link[0], _regContext, _blocks, this))
            {
                IsDispatch = true;
            }

            if (_analyzer.IsChildDispatcher(link[0], _mainDispatcher, _regContext))
            {
                IsDispatch = true;
            }
        }

        if (lastIns.Opcode() == OpCode.B && IsDispatch)
        {
            var mov = block.instructions[index + 1];
            if (mov.Opcode() == OpCode.MOV || mov.Opcode() == OpCode.MOVK)
            {
                return mov;
            }

            return null;
        }

        return null;
    }

    private List<Block> GetAllChildBlockNew(Block block)
    {
        if (block.isFind)
        {
            Logger.WarnNewline("block is Finding  " + block.start_address);
            return block.RealChilds;
        }

        block.isFind = true;
        var list = new List<Block>();
        bool HasCFF_CSEL = false;
        Instruction CFF_CSEL = null;
        bool IsUpdateDispatch = false;
        bool jumpB = false;
        bool CESLAfterMoveOpreand = false;
        foreach (var instruction in block.instructions)
        {
            switch (instruction.Opcode())
            {
                case OpCode.MOV:
                case OpCode.MOVK:
                {
                    if (_analyzer.IsRealBlockWithDispatchNextBlock(block, _mainDispatcher, _regContext, this))
                    {
                        SyncLogicInstruction(instruction);
                        IsUpdateDispatch = true;
                    }

                    if (HasCFF_CSEL)
                    {
                        CESLAfterMoveOpreand = true;
                    }

                    break;
                }
                case OpCode.CSEL:
                {
                    if (_analyzer.IsOperandDispatchRegister(instruction, _mainDispatcher, _regContext))
                    {
                        CFF_CSEL = instruction;
                        HasCFF_CSEL = true;
                        _regContext.SnapshotRegisters(block.start_address);
                        block.CFF_CSEL = instruction;
                        Logger.InfoNewline(" Have  CFF_CSEL " + instruction);
                        var CSELAfterMove = IsCSELAfterMove(block, instruction);
                        if (CSELAfterMove != null)
                        {
                            SyncLogicInstruction(CSELAfterMove);
                        }

                        var needOperandRegister = instruction.Operands()[0].registerName;
                        var operandLeft = instruction.Operands()[1].registerName;
                        var left = _regContext.GetRegister(operandLeft).GetLongValue();
                        _regContext.SetRegister(needOperandRegister, left);
                        var nextBlock = block.GetLinkedBlocks(this)[0];
                        var leftBlock = FindRealBlock(nextBlock);
                        list.Add(leftBlock);
                        _regContext.RestoreRegisters(block.start_address);
                        var operandRight = instruction.Operands()[2].registerName;
                        var right = _regContext.GetRegister(operandRight).GetLongValue();
                        _regContext.SetRegister(needOperandRegister, right);
                        if (CSELAfterMove != null)
                        {
                            SyncLogicInstruction(CSELAfterMove);
                        }

                        var rightBlock = FindRealBlock(nextBlock);
                        list.Add(rightBlock);
                    }


                    break;
                }
                case OpCode.B:
                {
                    jumpB = true;
                    //Got the link
                    var links = block.GetLinkedBlocks(this);
                    if (links.Count == 0)
                    {
                        break;
                    }

                    if (HasCFF_CSEL && instruction.GetAddress() - CFF_CSEL.GetAddress() == 4)
                    {
                        // LDR             X8, [SP,#0x70+var_48]
                        // CMP             X8, #0
                        // CSEL            W8, W24, W21, EQ
                        // B               loc_181E64
                        // it's CSEL after B  
                        break;
                    }

                    if (IsUpdateDispatch && HasCFF_CSEL && CESLAfterMoveOpreand)
                    {
                        block.CESLAfterMoveOpreand = true;
                        if (links.Count != 1)
                        {
                            throw new Exception(" CESLAfterMoveOpreand  but not only one link " + instruction);
                        }

                        var nextBlock = block.GetLinkedBlocks(this)[0];
                        var realBlock = FindRealBlock(nextBlock);
                        Logger.ErrorNewline("CESLAfterMoveOpreand  block  " + block.start_address + " is Link To   \n" +
                                            realBlock);
                        list.Add(realBlock);
                        break;
                    }

                    // loc_181E90
                    // LDR             Q0, [X26]
                    // MOV             X0, SP
                    // MOV             W1, #0x11
                    // STRB            W25, [SP,#0x70+var_60]
                    // STR             Q0, [SP,#0x70+var_70]
                    // BL              sub_1815C0
                    // MOV             W8, #0x16CE
                    // MOV             X4, X0
                    // STR             X0, [X22,#qword_7289F8@PAGEOFF]
                    // MOVK            W8, #0x8FEA,LSL#16
                    // B               loc_181E64
                    if (IsUpdateDispatch)
                    {
                        if (links.Count != 1)
                        {
                            throw new Exception(" Update Dispatch  but not only one link " + instruction);
                        }

                        var nextBlock = block.GetLinkedBlocks(this)[0];
                        var realBlock = FindRealBlock(nextBlock);
                        Logger.ErrorNewline("Update Dispatch  block  " + block.start_address + " is Link To   \n" +
                                            realBlock);
                        list.Add(realBlock);
                        break;
                    }

                    if (block.instructions.Count() == 1)
                    {
                        //[Block] 0x15ed28
                        // 0x15ed28   B loc_15F19C
                        //Fix this case
                        Logger.InfoNewline("is Only one instruction Block \n" + block);
                        list.AddRange(block.GetLinkedBlocks(this));
                        break;
                    }

                    //not Update Dispatch just link next block we need loop this block when this bransh end
                    Logger.ErrorNewline(" is not Update Dispatch just link next block " + instruction
                        + " block is \n" + block);
                    foreach (var link in links)
                    {
                        var realBlock = FindRealBlock(link);
                        Logger.InfoNewline("Find Block " + block.start_address + " Link To " + realBlock.start_address);
                        list.Add(realBlock);
                    }

                    break;
                }
            }
        }

        if (IsUpdateDispatch && !jumpB)
        {
            // LDR             X9, [SP,#0x2D0+var_2B0]
            // ADRP            X8, #qword_7289B8@PAGE
            // LDR             X8, [X8,#qword_7289B8@PAGEOFF]
            // STR             X9, [SP,#0x2D0+var_260]
            // LDR             X9, [SP,#0x2D0+var_2A8]
            // STR             X8, [SP,#0x2D0+var_238]
            // MOV             W8, #0x561D9EF8
            // STP             X19, X9, [SP,#0x2D0+var_270]
            //Fix this case
            var links = block.GetLinkedBlocks(this);
            if (links.Count != 1)
            {
                return list;
            }

            var nextBlock = block.GetLinkedBlocks(this)[0];
            var realBlock = FindRealBlock(nextBlock);
            Logger.ErrorNewline($"Not B Ins Find Block {block.start_address} Link To {realBlock.start_address}");
            list.Add(realBlock);
        }
        else
        {
            Logger.ErrorNewline("Warning  " + block.start_address + " is not Update Dispatch and not Jump  \n"+
                                block);   
            //got link Block
            var links = block.GetLinkedBlocks(this);
            if (links.Count == 0)
            {
                return list;
            }
            var realBlock = FindRealBlock(links[0]);
            Logger.ErrorNewline($" Not Update Dispatch and not Jump Find Block {block.start_address} Link To {realBlock.start_address}");
            list.Add(realBlock);
        }
        return list;
    }

    private Block RunDispatchBlock(Block block)
    {
        foreach (var instruction in block.instructions)
        {
            switch (instruction.Opcode())
            {
                case OpCode.MOV:
                {
                    //Runnning MOV instruction in Dispatch we need sync this
                    AssignRegisterByInstruction(instruction);
                    Logger.InfoNewline("MOV " + instruction.Operands()[0].registerName + " = " +
                                       instruction.Operands()[1].immediateValue + " in DispatchBlock ============");
                    break;
                }
                case OpCode.B:
                {
                    var addr = instruction.GetRelativeAddress();
                    var nextBlock = FindBlockByAddress(addr);
                    if (block.IsChildBlock(nextBlock))
                    {
                        return nextBlock;
                    }

                    throw new Exception(" B " + instruction + " is not in " + block);
                }
                case OpCode.CMP:
                {
                    var left = instruction.Operands()[0];
                    var right = instruction.Operands()[1];
                    _regContext.Compare(left.registerName, right.registerName);
                    _lastCompareIns = instruction;
                    break;
                }
                case OpCode.B_NE:
                case OpCode.B_EQ:
                case OpCode.B_GT:
                case OpCode.B_LE:
                {
                    var needJump = ConditionJumpHelper.Condition(instruction.Opcode(), _lastCompareIns, _regContext);
                    Block jumpBlock;
                    //next block is current Address +4 ;

                    jumpBlock = !needJump
                        ? FindBlockByAddress(instruction.GetAddress() + 4)
                        : FindBlockByAddress(instruction.GetRelativeAddress());
                    Logger.VerboseNewline("\n block  " + block + "\n is Jump ? " + needJump + " next block is " +
                                          jumpBlock.start_address);

                    if (block.IsChildBlock(jumpBlock))
                    {
                        return jumpBlock;
                    }

                    throw new Exception(
                        $" Analyze Error :  {jumpBlock.start_address} is not in {block.start_address} Child ");
                    // break;
                }
                default:
                {
                    throw new Exception(" not support opcode " + instruction.Opcode());
                }
            }
        }

        return null;
    }


    public Block FindBlockByAddress(long address)
    {
        foreach (var block in _blocks)
        {
            if (block.GetStartAddress() == address)
            {
                return block;
            }
        }

        return null;
    }

    private void AnalyzeInstruction()
    {
        ReBuildCFGBlocks();
    }

    private Register GetRegister(string name)
    {
        return _regContext.GetRegister(name);
    }

    private void AssignRegisterByInstruction(Instruction instruction)
    {
        switch (instruction.mnemonic)
        {
            case "MOV":
            {
                //Assign register
                var left = instruction.Operands()[0];
                var right = instruction.Operands()[1];
                if (left.kind == Arm64OperandKind.Register && right.kind == Arm64OperandKind.Immediate)
                {
                    //Assign immediate value to register
                    var register = GetRegister(left.registerName);
                    var imm = right.immediateValue;
                    register.value = imm;
                    Logger.ErrorNewline($"AssignRegisterByInstruction MOV {left.registerName} = {imm} ({imm:X})");
                }
            }
                break;
            case "MOVK":
            {
                var dest = instruction.Operands()[0];
                var imm = instruction.Operands()[1].immediateValue;
                var shift = instruction.Operands()[2].shiftType;
                var reg = GetRegister(dest.registerName);
                var v = MathHelper.CalculateMOVK(reg.GetLongValue(), imm, shift, instruction.Operands()[2].shiftValue);
                reg.value = v;
                Logger.InfoNewline($"AssignRegisterByInstruction MOVK {dest.registerName} = {imm} ({imm:X})");
                break;
            }
        }
    }
}