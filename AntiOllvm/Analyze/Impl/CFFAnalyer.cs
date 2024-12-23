using AntiOllvm.analyze;
using AntiOllvm.entity;
using AntiOllvm.Extension;
using AntiOllvm.Helper;
using AntiOllvm.Logging;

namespace AntiOllvm.Analyze.Type;

/**
 * Control Flow Flattening Analyer
 */
public class CFFAnalyer : IAnalyze
{
    private Block _findMain = null;

    private List<Block> _childDispatcher = new();
    private Config _config;
    
    private List<string> _childMainCompareRegister = new();
    public CFFAnalyer(Config config)
    {
        _config = config;
    }


    public void InitAfterRegisterAssign(RegisterContext context, Block main, List<Block> allBlocks,
        Simulation simulation)
    {
    }

    public bool IsMainDispatcher(Block block, RegisterContext context, List<Block> allBlocks, Simulation simulation)
    {
        if (_findMain == null)
        {
            _findMain = MainDispatchFinder.SmartFindMainDispatcher(block, context, allBlocks, simulation);
            if (_findMain == null)
            {
                throw new Exception(" can't find main dispatcher you should find in other way");
            }

            Logger.InfoNewline(" Find Main Dispatcher : " + _findMain.start_address);
        }

        return block.start_address == _findMain.start_address;
    }


    private void AddCacheChildDispatcher(Block block)
    {
        if (!_childDispatcher.Contains(block))
        {
            _childDispatcher.Add(block);
        }
    }

    private bool CheckIsCMPAndJumpToNext(Block block, RegisterContext context)
    {
        if (block.instructions.Count == 2)
        {
            bool hasCMP = false;
            bool hasBCond = false;
            foreach (var instruction in block.instructions)
            {
                switch (instruction.Opcode())
                {
                    case OpCode.CMP:
                    {
                        var first = instruction.Operands()[0].registerName;
                        var second = instruction.Operands()[1].registerName;
                        if (_childMainCompareRegister.Contains(first))
                        {
                            var leftImm= context.GetRegister(first).GetLongValue();
                            var rightImm = context.GetRegister(second).GetLongValue();
                            if (leftImm != 0 && rightImm != 0)
                            {
                                hasCMP = true;
                            }
                        }

                        break;
                    }
                    case OpCode.B_NE:
                    case OpCode.B_EQ:
                    case OpCode.B_GT:
                    case OpCode.B_LE:
                    {
                        hasBCond = true;
                        break;
                    }
                }
            }

            if (hasBCond && hasCMP)
            {
                return true;
            }
        }

        if (block.instructions.Count==3)
        {
            bool hasCMP = false;
            bool hasBCond = false;
            string moveImmReg = "";
            foreach (var instruction in block.instructions)
            {
                switch (instruction.Opcode())
                {
                    case OpCode.MOVK:
                    case OpCode.MOV:
                    {
                        var op = instruction.Operands()[1];
                        if (op.kind == Arm64OperandKind.Immediate && op.immediateValue != 0)
                        {
                            moveImmReg = instruction.Operands()[0].registerName;
                        }
                        break;
                    }
                    case OpCode.CMP:
                    {
                        var first = instruction.Operands()[0].registerName;
                        var second = instruction.Operands()[1].registerName;
                        Logger.InfoNewline("moveImmReg "+moveImmReg +" first "+first+" second "+second);
                        if (_childMainCompareRegister.Contains(first) && second == moveImmReg)
                        {
                            hasCMP = true;
                        }

                        break;
                    }
                    case OpCode.B_NE:
                    case OpCode.B_EQ:
                    case OpCode.B_GT:
                    case OpCode.B_LE:
                    {
                        hasBCond = true;
                        break;
                    }
                }
            }

            if (hasBCond && hasCMP)
            {
                return true;
            }
                
        }

        return false;
    }
    private bool CheckIsMoveImmAndJumpToDispatcher(Block curBlock)
    {
        bool hasMovImm = false;
        bool isJumpDispatcher = false;
        foreach (var instruction in curBlock.instructions)
        {
            switch (instruction.Opcode())
            {
                case OpCode.MOVK:
                case OpCode.MOV:
                {
                    var op = instruction.Operands()[1];
                    if (op.kind == Arm64OperandKind.Immediate && op.immediateValue != 0)
                    {
                        hasMovImm = true;
                    }

                    break;
                }
                case OpCode.B:
                {
                    var address = instruction.GetRelativeAddress();
                    if (_findMain.GetStartAddress() == address)
                    {
                        isJumpDispatcher = true;
                    }

                    if (_childDispatcher.Exists(x => x.GetStartAddress() == address))
                    {
                        isJumpDispatcher = true;
                    }

                    break;
                }
            }
        }

        if (hasMovImm && isJumpDispatcher)
        {
            return true;
        }

        return false;
    }

    /**
     *
     */
    public bool IsChildDispatcher(Block curBlock, Block mainBlock, RegisterContext registerContext)
    {
        if (_childDispatcher.Contains(curBlock))
        {
            return true;
        }
        var isChild1 = ChildDispatchFinder.IsChildDispatch1(curBlock, mainBlock, registerContext);
        if (isChild1)
        {
            AddCacheChildDispatcher(curBlock);
            return true;
        }

        var isChild2 = ChildDispatchFinder.IsChildDispatch2(curBlock, mainBlock, registerContext);
        if (isChild2)
        {
            AddCacheChildDispatcher(curBlock);
            return true;
        }
        // 0x17f848   MOV W8,#0x67DE569C
        if (curBlock.instructions.Count==1)
        {
            //is only MOV Register to dispatcher ?
            if (curBlock.instructions[0].Opcode() == OpCode.MOV)
            {
                var second = curBlock.instructions[0].Operands()[1];
                if (second.kind == Arm64OperandKind.Immediate && second.immediateValue != 0)
                {
                    AddCacheChildDispatcher(curBlock);
                    return true;
                }
            }
        }
        if (curBlock.instructions.Count == 2)
        {   
            //MOV             W10, #0x8614E721
            // B               loc_17F59C
            if (CheckIsMoveImmAndJumpToDispatcher(curBlock))
            {
                AddCacheChildDispatcher(curBlock);
                return true;
            }
            // 0x17f5f0   CMP W8,W9
            // 0x17f5f4   B.EQ loc_17F6A8
            if (CheckIsCMPAndJumpToNext(curBlock,registerContext))
            {
                AddCacheChildDispatcher(curBlock);
                return true;
            }
        }
        //0x17f5e8   MOV W9,#0x90A6161B
        // 0x17f5f0   CMP W8,W9
        // 0x17f5f4   B.EQ loc_17F6A8
        if (curBlock.instructions.Count == 3)
        {
            if (CheckIsCMPAndJumpToNext(curBlock,registerContext))
            {
                AddCacheChildDispatcher(curBlock);
                return true;
            }
        }
        if (_config.force_no_child_main)
        {
            return false;
        }

        return false;
    }

    public bool IsRealBlock(Block block, Block mainBlock, RegisterContext context)
    {
        return true;
    }

    /**
     * Return real block has child block
     */
    public bool IsRealBlockWithDispatchNextBlock(Block block, Block mainDispatcher, RegisterContext regContext,
        Simulation simulation)
    {
        var mainRegisterName = mainDispatcher.GetMainDispatchOperandRegisterName();
        foreach (var instruction in block.instructions)
        {
            if (instruction.Opcode() == OpCode.B &&
                instruction.GetRelativeAddress() == mainDispatcher.GetStartAddress())
            {
                return true;
            }
        }

        // loc_15E604
        // LDR             X9, [SP,#0x2D0+var_2B0]
        // ADRP            X8, #qword_7289B8@PAGE
        // LDR             X8, [X8,#qword_7289B8@PAGEOFF]
        // STR             X9, [SP,#0x2D0+var_260]
        // LDR             X9, [SP,#0x2D0+var_2A8]
        // STR             X8, [SP,#0x2D0+var_238]
        // MOV             W8, #0x561D9EF8
        // STP             X19, X9, [SP,#0x2D0+var_270]
        foreach (var instruction in block.instructions)
        {
            if (instruction.Opcode() == OpCode.MOV || instruction.Opcode() == OpCode.MOVK)
            {
                var second = instruction.Operands()[1];

                if (instruction.Operands()[0].registerName == mainRegisterName &&
                    second.kind == Arm64OperandKind.Immediate)
                {
                    return true;
                }
            }
        }

        if (_config.force_no_child_main)
        {
            return false;
        }

        //  if one function has multi compare Register  we need fix this case
        bool isMoveImm = false;
        string moveRegister = "";
        foreach (var instruction in block.instructions)
        {
            if (instruction.Opcode() == OpCode.MOV || instruction.Opcode() == OpCode.MOVK)
            {
                var second = instruction.Operands()[1];

                if (second.kind == Arm64OperandKind.Immediate && second.immediateValue != 0)
                {
                    moveRegister = instruction.Operands()[0].registerName;
                    isMoveImm = true;
                }
            }
        }

        if (isMoveImm)
        {
            //got next is dispatcher ? 
            var links = block.GetLinkedBlocks(simulation);
            if (links.Count != 1)
            {
                return false;
            }

            var link = links[0];
            if (IsCompareRegister(link, moveRegister))
            {   
                //Add to child dispatcher
                AddCacheChildDispatcher(link);
                //it's the first show in this function we need add to child main compare register
                if (!_childMainCompareRegister.Contains(moveRegister))
                {
                    _childMainCompareRegister.Add(moveRegister);
                }
                return true;
            }
        }

        return false;
    }

    /** it‘s change compare register to W8  it's child dispatcher too
     * loc_17F61C
    CMP             W8, W23
    B.EQ            loc_17F640
     */
    private bool IsCompareRegister(Block block, string registerName)
    {
        if (block.instructions.Count == 2)
        {
            bool hasCMP = false;
            bool hasBCond = false;
            foreach (var instruction in block.instructions)
            {
                switch (instruction.Opcode())
                {
                    case OpCode.CMP:
                    {
                        var first = instruction.Operands()[0].registerName;
                        if (first == registerName)
                        {
                            hasCMP = true;
                        }

                        break;
                    }
                    case OpCode.B_NE:
                    case OpCode.B_EQ:
                    case OpCode.B_GT:
                    case OpCode.B_LE:
                    {
                        hasBCond = true;
                        break;
                    }
                }
            }

            if (hasBCond && hasCMP)
            {
                return true;
            }
        }


        return false;
    }

    public bool IsOperandDispatchRegister(Instruction instruction, Block mainDispatcher, RegisterContext regContext)
    {
        var mainCompareName = mainDispatcher.GetMainDispatchOperandRegisterName();
        var first = instruction.Operands()[0].registerName;
        var second = instruction.Operands()[1].registerName;
        var third = instruction.Operands()[2].registerName;
        if (mainCompareName == first)
        {
            var secondReg = regContext.GetRegister(second);
            var thirdReg = regContext.GetRegister(third);
            if (secondReg.GetLongValue() != 0 && thirdReg.GetLongValue() != 0)
            {
                // Logger.InfoNewline("Find CFF_CSEL_Expression " + instruction);
                return true;
            }
        }

        return false;
    }
}