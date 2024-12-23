using AntiOllvm.entity;
using AntiOllvm.Extension;
using AntiOllvm.Logging;

namespace AntiOllvm.Helper;

public static class ChildDispatchFinder
{
 
    /**
     * Find Child when this block only 2 instruction
     * CMP W8,W9
     * B.EQ 0X100
     */
    public static bool IsChildDispatch1(Block curBlock, Block mainBlock, RegisterContext registerContext)
    {
        var operandRegName = mainBlock.GetMainDispatchOperandRegisterName();
        foreach (var instruction in curBlock.instructions)
        {
            switch (instruction.Opcode())
            {
                case OpCode.CMP:
                {
                    var operand = instruction.Operands()[0];

                    var right = instruction.Operands()[1];
                    if (right is { kind: Arm64OperandKind.Immediate })
                    {
                        return false;
                    }

                    var imm = registerContext.GetRegister(right.registerName).GetLongValue();

                    if (operandRegName == operand.registerName && imm != 0 &&
                        curBlock.instructions.Count == mainBlock.instructions.Count)
                    {
                        return true;
                    }

                    return false;
                }
            }
        }

        return false;
    }

    /**
     * Find Child in this case
     * loc_15E5A8
        MOV         W9, #0xD9210058
        CMP         W8, W9
        B.EQ        loc_15E604
     */
    public static bool IsChildDispatch2(Block curBlock, Block mainBlock, RegisterContext registerContext)
    {
        var operandRegName = mainBlock.GetMainDispatchOperandRegisterName();
        foreach (var instruction in curBlock.instructions)
        {
            switch (instruction.Opcode())
            {
                case OpCode.CMP:
                {
                    var operand = instruction.Operands()[0];

                    var right = instruction.Operands()[1];
                    if (operand.registerName == operandRegName)
                    {
                        if (right.kind == Arm64OperandKind.Register)
                        {
                            var isImmToReg = IsMoveImmediateToRegister(curBlock, mainBlock, registerContext,
                                out var registerName);
                            if (isImmToReg && registerName == right.registerName)
                            {
                                return true;
                            }
                        }
                    }

                    return false;
                }
            }
        }

        return false;
    }

    private static bool IsMoveImmediateToRegister(Block block, Block mainBlock, RegisterContext registerContext,
        out string registerName)
    {
        registerName = "";
        foreach (var instruction in block.instructions)
        {
            switch (instruction.Opcode())
            {
                case OpCode.MOV:
                {
                    var operand = instruction.Operands()[0];
                    if (operand.kind == Arm64OperandKind.Register)
                    {
                        var right = instruction.Operands()[1];
                        if (right is { kind: Arm64OperandKind.Immediate, immediateValue: 0 })
                        {
                            return false;
                        }

                        if (right.kind == Arm64OperandKind.Register)
                        {
                            return false;
                        }

                        registerName = operand.registerName;
                        return true;
                    }

                    return false;
                }
            }
        }

        return false;
    }

    /**
     * 0x17f61c   CMP W8,W23
      0x17f620   B.EQ loc_17F640
     */
    private static bool HasChildMainChild1(Block block, RegisterContext context,
        List<string> childOperandsName, List<Block> multiChildMainBlocks)
    {
        bool isCompare = false;
        bool isHaveConditionJump = false;
        foreach (var instruction in block.instructions)
        {
            switch (instruction.Opcode())
            {
                case OpCode.CMP:
                {
                    //CMP W8,W24
                    var left = instruction.Operands()[0];
                    var right = instruction.Operands()[1];
                    if (left.kind == Arm64OperandKind.Register && right.kind == Arm64OperandKind.Register)
                    {
                        var leftReg = context.GetRegister(left.registerName);
                        var rightReg = context.GetRegister(right.registerName);
                        if (leftReg.GetLongValue() != 0 && rightReg.GetLongValue() != 0)
                        {
                            //Left name must in childOperandsName
                            if (childOperandsName.Contains(left.registerName))
                            {
                                isCompare = true;
                            }
                        }
                    }

                    break;
                }
                case OpCode.B_NE:
                case OpCode.B_EQ:
                case OpCode.B_GT:
                case OpCode.B_LE:
                {
                    isHaveConditionJump = true;
                    break;
                }
            }
        }

        if (isCompare && isHaveConditionJump)
        {
            return true;
        }

        return false;
    }

    /**
     MOV W23, #0XE123456
     CMP W8,W23
     B.EQ loc_17F640
     */
    private static bool HasChildDispatcher3(Block block, RegisterContext context,
        List<string> childOperandsName)
    {
        bool isCompare = false;
        bool isHaveConditionJump = false;
        bool isMovImm = false;
        string lastMoveRegisterName = "";
        foreach (var instruction in block.instructions)
        {
            switch (instruction.Opcode())
            {
                case OpCode.MOV:
                case OpCode.MOVK:
                {
                    // MOV             W9, #0x90A6161B
                    var left = instruction.Operands()[0];
                    var right = instruction.Operands()[1];
                    if (left.kind == Arm64OperandKind.Register && right.kind == Arm64OperandKind.Immediate)
                    {
                        if (right.immediateValue != 0)
                        {
                            isMovImm = true;
                            lastMoveRegisterName = left.registerName;
                        }
                    }

                    break;
                }
                case OpCode.CMP:
                {
                    //CMP W8,W24
                    var left = instruction.Operands()[0];
                    var right = instruction.Operands()[1];
                    if (childOperandsName.Contains(left.registerName) && right.registerName == lastMoveRegisterName)
                    {
                        isCompare = true;
                    }

                    break;
                }
                case OpCode.B_NE:
                case OpCode.B_EQ:
                case OpCode.B_GT:
                case OpCode.B_LE:
                {
                    isHaveConditionJump = true;
                    break;
                }
            }
        }

        if (isCompare && isHaveConditionJump && isMovImm)
        {
            return true;
        }

        return false;
    }
  /** 
   *  W8 is ChildMainDispatcher
    // 0x17f634   MOV W8,#0x43E7558A
    // 0x17f63c   B loc_17F5E0
   */
    private static bool HasChildMainChild2(Block block, RegisterContext context,
        List<string> childOperandsName, List<Block> multiChildMainBlocks)
    {   
        bool isMovMainDispatcher = false;
        bool isJumpToMain = false;
        foreach (var instruction in block.instructions)
        {
            switch (instruction.Opcode())
            {
                case OpCode.B:
                {
                    var addr= instruction.GetRelativeAddress();
                    if (multiChildMainBlocks.Exists(x => x.GetStartAddress() == addr))
                    {
                        isJumpToMain = true;
                    }
                    break;
                }
                case OpCode.MOVK:
                case OpCode.MOV:
                {
                    var left = instruction.Operands()[0];
                    var right = instruction.Operands()[1];
                    if (childOperandsName.Contains(left.registerName) && right.kind == Arm64OperandKind.Immediate)
                    {
                        isMovMainDispatcher = true;
                    }
                    break;
                }
            }
        }

        if (isJumpToMain && isMovMainDispatcher)
        {
            return true;
        }
        return false;
    }

    private static bool HasChildMainChildDispatcherFlag(Block block, RegisterContext context,
        List<string> childOperandsName, List<Block> multiChildMainBlocks)
    {
        if (block.instructions.Count == 2)
        {
            if (HasChildMainChild1(block, context, childOperandsName, multiChildMainBlocks))
            {
                return true;
            }

            if (HasChildMainChild2(block, context, childOperandsName, multiChildMainBlocks))
            {
                return true;
            }
        }

        if (block.instructions.Count == 3)
        {
            if (HasChildDispatcher3(block, context, childOperandsName))
            {
                return true;
            }
        }

        return false;
    }
    
    /**
     * 0x17f754   MOV W10,#0x8614E721
     0x17f75c   B loc_17F59C
     */
    
    
    
    /** //Find in this case
     * MOV             W9, #0xF0EA4675
      CMP              W8, W9
      B.LE             loc_17E5BC
     */
    public static bool IsHasChildDispatcherFlagWithMoveRegisterAndCMP(Block block, RegisterContext registerContext,
        string operandsName)
    {
        var childOperandsName = new List<string> { operandsName };
        return HasChildDispatcher3(block, registerContext, childOperandsName);
    }
  
    public static bool IsChildMainChildDispatch(Block curBlock, RegisterContext registerContext,
        List<string> childOperandsName, List<Block> multiChildMainBlocks)
    {
        return HasChildMainChildDispatcherFlag(curBlock, registerContext, childOperandsName, multiChildMainBlocks);
    }
}