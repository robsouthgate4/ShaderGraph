//using System.Reflection;
using System;
using System.Collections.Generic;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph.Drawing.Slots;
using UnityEngine;
using UnityEngine.Experimental.UIElements;
using System.Linq;

namespace UnityEditor.ShaderGraph
{
    [Title("Math", "Basic", "Multiply")]
    public class MultiplyNode : AbstractMaterialNode, IGeneratesBodyCode, IGeneratesFunction
    {
        public MultiplyNode()
        {
            name = "Multiply";
            UpdateNodeAfterDeserialization();
        }

        const int Input1SlotId = 0;
        const int Input2SlotId = 1;
        const int OutputSlotId = 2;
        const string kInput1SlotName = "A";
        const string kInput2SlotName = "B";
        const string kOutputSlotName = "Out";

        public override bool hasPreview
        {
            get { return m_MultiplyType != MultiplyType.Matrix; }
            //get { return false; }
        }

        string GetFunctionName()
        {
            return string.Format("Unity_Multiply_{0}_{1}", 
                FindInputSlot<MaterialSlot>(Input1SlotId).concreteValueType.ToString(precision),
                FindInputSlot<MaterialSlot>(Input2SlotId).concreteValueType.ToString(precision));
        }

        public sealed override void UpdateNodeAfterDeserialization()
        {
            AddSlot(new DynamicValueMaterialSlot(Input1SlotId, kInput1SlotName, kInput1SlotName, SlotType.Input, Matrix4x4.zero));
            AddSlot(new DynamicValueMaterialSlot(Input2SlotId, kInput2SlotName, kInput2SlotName, SlotType.Input, new Matrix4x4(new Vector4(2,2,2,2), Vector4.zero, Vector4.zero, Vector4.zero)));
            AddSlot(new DynamicValueMaterialSlot(OutputSlotId, kOutputSlotName, kOutputSlotName, SlotType.Output, Matrix4x4.zero));
            RemoveSlotsNameNotMatching(new[] { Input1SlotId, Input2SlotId, OutputSlotId });
        }

        public void GenerateNodeCode(ShaderGenerator visitor, GenerationMode generationMode)
        {
            var sb = new ShaderStringBuilder();
            var input1Value = GetSlotValue(Input1SlotId, generationMode);
            var input2Value = GetSlotValue(Input2SlotId, generationMode);
            var outputValue = GetSlotValue(OutputSlotId, generationMode);

            sb.AppendLine("{0} {1};", NodeUtils.ConvertConcreteSlotValueTypeToString(precision, FindOutputSlot<MaterialSlot>(OutputSlotId).concreteValueType), GetVariableNameForSlot(OutputSlotId));
            sb.AppendLine("{0}({1}, {2}, {3});", GetFunctionName(), input1Value, input2Value, outputValue);

            visitor.AddShaderChunk(sb.ToString(), false);
        }

        public void GenerateNodeFunction(FunctionRegistry registry, GenerationMode generationMode)
        {
            registry.ProvideFunction(GetFunctionName(), s =>
            {
                s.AppendLine("void {0} ({1} A, {2} B, out {3} Out)",
                    GetFunctionName(),
                    FindInputSlot<MaterialSlot>(Input1SlotId).concreteValueType.ToString(precision),
                    FindInputSlot<MaterialSlot>(Input2SlotId).concreteValueType.ToString(precision),
                    FindOutputSlot<MaterialSlot>(OutputSlotId).concreteValueType.ToString(precision));
                using (s.BlockScope())
                {
                    switch(m_MultiplyType)
                    {
                        case MultiplyType.Vector:
                            s.AppendLine("Out = A * B;");
                            break;
                        default:
                            s.AppendLine("Out = mul(A, B);");
                            break; 
                    }
                }
            });
        }

        // Internal validation
        // -------------------------------------------------

        public enum MultiplyType
        {
            Vector,
            Matrix,
            Mixed
        }

        MultiplyType m_MultiplyType;

        public override void ValidateNode()
        {
            var isInError = false;

            // all children nodes needs to be updated first
            // so do that here
            var slots = ListPool<MaterialSlot>.Get();
            GetInputSlots(slots);
            foreach (var inputSlot in slots)
            {
                inputSlot.hasError = false;

                var edges = owner.GetEdges(inputSlot.slotReference);
                foreach (var edge in edges)
                {
                    var fromSocketRef = edge.outputSlot;
                    var outputNode = owner.GetNodeFromGuid(fromSocketRef.nodeGuid);
                    if (outputNode == null)
                        continue;

                    outputNode.ValidateNode();
                    if (outputNode.hasError)
                        isInError = true;
                }
            }
            ListPool<MaterialSlot>.Release(slots);

            var dynamicInputSlotsToCompare = DictionaryPool<DynamicValueMaterialSlot, ConcreteSlotValueType>.Get();
            var skippedDynamicSlots = ListPool<DynamicValueMaterialSlot>.Get();

            // iterate the input slots
            s_TempSlots.Clear();
            GetInputSlots(s_TempSlots);
            foreach (var inputSlot in s_TempSlots)
            {
                // if there is a connection
                var edges = owner.GetEdges(inputSlot.slotReference).ToList();
                if (!edges.Any())
                {
                    if (inputSlot is DynamicValueMaterialSlot)
                        skippedDynamicSlots.Add(inputSlot as DynamicValueMaterialSlot);
                    continue;
                }

                // get the output details
                var outputSlotRef = edges[0].outputSlot;
                var outputNode = owner.GetNodeFromGuid(outputSlotRef.nodeGuid);
                if (outputNode == null)
                    continue;

                var outputSlot = outputNode.FindOutputSlot<MaterialSlot>(outputSlotRef.slotId);
                if (outputSlot == null)
                    continue;

                if (outputSlot.hasError)
                {
                    inputSlot.hasError = true;
                    continue;
                }

                var outputConcreteType = outputSlot.concreteValueType;
                // dynamic input... depends on output from other node.
                // we need to compare ALL dynamic inputs to make sure they
                // are compatable.
                if (inputSlot is DynamicValueMaterialSlot)
                {
                    dynamicInputSlotsToCompare.Add((DynamicValueMaterialSlot)inputSlot, outputConcreteType);
                    continue;
                }

                // if we have a standard connection... just check the types work!
                if (!ImplicitConversionExists(outputConcreteType, inputSlot.concreteValueType))
                    inputSlot.hasError = true;
            }

            m_MultiplyType = GetMultiplyType(dynamicInputSlotsToCompare.Values);

            // Resolve dynamics depending on matrix/vector configuration
            switch(m_MultiplyType)
            {
                // If all matrix resolve as per dynamic matrix
                case MultiplyType.Matrix:
                    var dynamicMatrixType = ConvertDynamicMatrixInputTypeToConcrete(dynamicInputSlotsToCompare.Values);
                    foreach (var dynamicKvP in dynamicInputSlotsToCompare)
                        dynamicKvP.Key.SetConcreteType(dynamicMatrixType);
                    foreach (var skippedSlot in skippedDynamicSlots)
                        skippedSlot.SetConcreteType(dynamicMatrixType);
                    break;
                // If mixed handle differently:
                // Iterate all slots and set their concretes based on their edges
                // Find matrix slot and convert its type to a vector type
                // Reiterate all slots and set non matrix slots to the vector type
                case MultiplyType.Mixed:
                    foreach (var dynamicKvP in dynamicInputSlotsToCompare)
                    {
                        SetConcreteValueTypeFromEdge(dynamicKvP.Key);
                    }
                    MaterialSlot matrixSlot = GetMatrixSlot();
                    ConcreteSlotValueType vectorType = ConvertMatrixToVectorType(matrixSlot.concreteValueType);
                    foreach (var dynamicKvP in dynamicInputSlotsToCompare)
                    {
                        if(dynamicKvP.Key != matrixSlot)
                            dynamicKvP.Key.SetConcreteType(vectorType);
                    }
                    foreach (var skippedSlot in skippedDynamicSlots)
                    {
                        skippedSlot.SetConcreteType(vectorType);
                    }
                    break;
                // If all vector resolve as per dynamic vector
                default:
                    var dynamicVectorType = ConvertDynamicInputTypeToConcrete(dynamicInputSlotsToCompare.Values);
                    foreach (var dynamicKvP in dynamicInputSlotsToCompare)
                        dynamicKvP.Key.SetConcreteType(dynamicVectorType);
                    foreach (var skippedSlot in skippedDynamicSlots)
                        skippedSlot.SetConcreteType(dynamicVectorType);
                    break;
            }

            s_TempSlots.Clear();
            GetInputSlots(s_TempSlots);
            var inputError = s_TempSlots.Any(x => x.hasError);

            // configure the output slots now
            // their slotType will either be the default output slotType
            // or the above dynanic slotType for dynamic nodes
            // or error if there is an input error
            s_TempSlots.Clear();
            GetOutputSlots(s_TempSlots);
            foreach (var outputSlot in s_TempSlots)
            {
                outputSlot.hasError = false;

                if (inputError)
                {
                    outputSlot.hasError = true;
                    continue;
                }

                if (outputSlot is DynamicValueMaterialSlot)
                {
                    // Apply similar logic to output slot
                    switch(m_MultiplyType)
                    {
                        // As per dynamic matrix
                        case MultiplyType.Matrix:
                            var dynamicMatrixType = ConvertDynamicMatrixInputTypeToConcrete(dynamicInputSlotsToCompare.Values);
                            (outputSlot as DynamicValueMaterialSlot).SetConcreteType(dynamicMatrixType);
                            break;
                        // Mixed configuration
                        // Find matrix slot and convert type to vector
                        // Set output concrete to vector
                        case MultiplyType.Mixed:
                            MaterialSlot matrixSlot = GetMatrixSlot();
                            ConcreteSlotValueType vectorType = ConvertMatrixToVectorType(matrixSlot.concreteValueType);
                            (outputSlot as DynamicValueMaterialSlot).SetConcreteType(vectorType);
                            break;
                        // As per dynamic vector
                        default:
                            var dynamicVectorType = ConvertDynamicInputTypeToConcrete(dynamicInputSlotsToCompare.Values);
                            (outputSlot as DynamicValueMaterialSlot).SetConcreteType(dynamicVectorType);
                            break;
                    }
                    continue;
                }
            }

            isInError |= inputError;
            s_TempSlots.Clear();
            GetOutputSlots(s_TempSlots);
            isInError |= s_TempSlots.Any(x => x.hasError);
            isInError |= CalculateNodeHasError();
            hasError = isInError;

            if (!hasError)
            {
                ++version;
            }

            ListPool<DynamicValueMaterialSlot>.Release(skippedDynamicSlots);
            DictionaryPool<DynamicValueMaterialSlot, ConcreteSlotValueType>.Release(dynamicInputSlotsToCompare);

            List<MaterialSlot> inSlots = new List<MaterialSlot>();
            List<MaterialSlot> outSlots = new List<MaterialSlot>();
            GetInputSlots<MaterialSlot>(inSlots);
            GetOutputSlots<MaterialSlot>(outSlots);

            // Debugs
            foreach(MaterialSlot i in inSlots)
                Debug.Log("Node: "+this.guid +" slot "+i.displayName+" to type "+i.concreteValueType);
            foreach(MaterialSlot o in outSlots)
                Debug.Log("Node: "+this.guid +" slot "+o.displayName+" to type "+o.concreteValueType);
        }

        private static bool ImplicitConversionExists(ConcreteSlotValueType from, ConcreteSlotValueType to)
        {
            if (from == to)
                return true;

            var fromCount = SlotValueHelper.GetChannelCount(from);
            var toCount = SlotValueHelper.GetChannelCount(to);


            // can convert from v1 vectors :)
            if (from == ConcreteSlotValueType.Vector1 && toCount > 0)
                return true;

            if (toCount == 0)
                return false;

            if (toCount <= fromCount)
                return true;

            return false;
        }

        private MultiplyType GetMultiplyType(IEnumerable<ConcreteSlotValueType> inputTypes)
        {
            var concreteSlotValueTypes = inputTypes as List<ConcreteSlotValueType> ?? inputTypes.ToList();
            int matrixCount = 0;
            int vectorCount = 0;
            for (int i = 0; i < concreteSlotValueTypes.Count; i++)
            {
                if(concreteSlotValueTypes[i] == ConcreteSlotValueType.Vector4
                    || concreteSlotValueTypes[i] == ConcreteSlotValueType.Vector3
                    || concreteSlotValueTypes[i] == ConcreteSlotValueType.Vector2
                    || concreteSlotValueTypes[i] == ConcreteSlotValueType.Vector1)
                {
                    vectorCount++;
                }
                else if(concreteSlotValueTypes[i] == ConcreteSlotValueType.Matrix4
                    || concreteSlotValueTypes[i] == ConcreteSlotValueType.Matrix3
                    || concreteSlotValueTypes[i] == ConcreteSlotValueType.Matrix2)
                {
                    matrixCount++;
                }
            }
            if(matrixCount == 2)
                return MultiplyType.Matrix;
            else if(vectorCount == 2)
                return MultiplyType.Vector;
            else if(matrixCount == 1)
                return MultiplyType.Mixed;
            else
                return MultiplyType.Vector;
        }

        private MaterialSlot GetMatrixSlot()
        {
            List<MaterialSlot> slots = new List<MaterialSlot>();
            GetInputSlots(slots);
            for (int i = 0; i < slots.Count; i++)
            {
                var edges = owner.GetEdges(slots[i].slotReference).ToList();
                if(!edges.Any())
                    continue;
                var outputNode = owner.GetNodeFromGuid(edges[0].outputSlot.nodeGuid);
                var outputSlot = outputNode.FindOutputSlot<MaterialSlot>(edges[0].outputSlot.slotId);
                if(outputSlot.concreteValueType == ConcreteSlotValueType.Matrix4
                    || outputSlot.concreteValueType == ConcreteSlotValueType.Matrix3
                    || outputSlot.concreteValueType == ConcreteSlotValueType.Matrix2)
                return slots[i];
            }
            return null;
        }

        private void SetConcreteValueTypeFromEdge(DynamicValueMaterialSlot slot)
        {
            var edges = owner.GetEdges(slot.slotReference).ToList();
            if(!edges.Any())
                return;
            var outputNode = owner.GetNodeFromGuid(edges[0].outputSlot.nodeGuid);
            var outputSlot = outputNode.FindOutputSlot<MaterialSlot>(edges[0].outputSlot.slotId);
            slot.SetConcreteType(outputSlot.concreteValueType);
        }

        private ConcreteSlotValueType ConvertMatrixToVectorType(ConcreteSlotValueType matrixType)
        {
            switch(matrixType)
            {
                case ConcreteSlotValueType.Matrix4:
                    return ConcreteSlotValueType.Vector4;
                case ConcreteSlotValueType.Matrix3:
                    return ConcreteSlotValueType.Vector3;
                default:
                    return ConcreteSlotValueType.Vector2;
            }
        }

        private ConcreteSlotValueType ConvertDynamicInputTypeToConcrete(IEnumerable<ConcreteSlotValueType> inputTypes)
        {
            var concreteSlotValueTypes = inputTypes as IList<ConcreteSlotValueType> ?? inputTypes.ToList();
            var inputTypesDistinct = concreteSlotValueTypes.Distinct().ToList();
            switch (inputTypesDistinct.Count)
            {
                case 0:
                    return ConcreteSlotValueType.Vector1;
                case 1:
                    return inputTypesDistinct.FirstOrDefault();
                default:
                    // find the 'minumum' channel width excluding 1 as it can promote
                    inputTypesDistinct.RemoveAll(x => x == ConcreteSlotValueType.Vector1);
                    var ordered = inputTypesDistinct.OrderByDescending(x => x);
                    if (ordered.Any())
                        return ordered.FirstOrDefault();
                    break;
            }
            return ConcreteSlotValueType.Vector1;
        }

        private ConcreteSlotValueType ConvertDynamicMatrixInputTypeToConcrete(IEnumerable<ConcreteSlotValueType> inputTypes)
        {
            var concreteSlotValueTypes = inputTypes as IList<ConcreteSlotValueType> ?? inputTypes.ToList();
            var inputTypesDistinct = concreteSlotValueTypes.Distinct().ToList();
            switch (inputTypesDistinct.Count)
            {
                case 0:
                    return ConcreteSlotValueType.Matrix2;
                case 1:
                    return inputTypesDistinct.FirstOrDefault();
                default:
                    var ordered = inputTypesDistinct.OrderByDescending(x => x);
                    if (ordered.Any())
                        return ordered.FirstOrDefault();
                    break;
            }
            return ConcreteSlotValueType.Matrix2;
        }

        /*private ConcreteSlotValueType ConvertDynamicOutputType(List<ConcreteSlotValueType> inputTypesDistinct)
        {
            // If dynamics contain vectors return a vector
            if(DynamicsContainVectors(inputTypesDistinct))
            {
                int vectorLength;
                // If mul(matrix, vector) return matrix length
                if(DynamicsContainMatrices(inputTypesDistinct, out vectorLength))
                {
                    switch(vectorLength)
                    {
                        case 2:
                            return ConcreteSlotValueType.Vector2;
                        case 3:
                            return ConcreteSlotValueType.Vector3;
                        case 4:
                            return ConcreteSlotValueType.Vector4;
                        default:
                            return ConcreteSlotValueType.Vector1;
                    }
                }
                
                // find the 'minumum' channel width excluding 1 as it can promote
                inputTypesDistinct.RemoveAll(x => x == ConcreteSlotValueType.Vector1);
                var ordered = inputTypesDistinct.OrderByDescending(x => x);
                if (ordered.Any())
                    return ordered.FirstOrDefault();
                return ConcreteSlotValueType.Vector1;
            }
            // Otherwise return a matrix
            else
            {
                var ordered = inputTypesDistinct.OrderByDescending(x => x);
                if (ordered.Any())
                    return ordered.FirstOrDefault();
                return ConcreteSlotValueType.Vector1;
            }
        }

        private bool DynamicsContainVectors(IList<ConcreteSlotValueType> inputTypes)
        {
            for (int i = 0; i < inputTypes.Count; i++)
            {
                if(inputTypes[i] == ConcreteSlotValueType.Vector4
                    || inputTypes[i] == ConcreteSlotValueType.Vector3
                    || inputTypes[i] == ConcreteSlotValueType.Vector2
                    || inputTypes[i] == ConcreteSlotValueType.Vector1)
                {
                    return true;
                }

            }
            return false;
        }

        private bool DynamicsContainMatrices(IList<ConcreteSlotValueType> inputTypes, out int vectorLength)
        {
            for (int i = 0; i < inputTypes.Count; i++)
            {
                if(DynamicIsMatrix(inputTypes[i], out vectorLength))
                    return true;
            }
            vectorLength = 0;
            return false;
        }

        private bool DynamicIsMatrix(ConcreteSlotValueType type, out int vectorLength)
        {
            if(type == ConcreteSlotValueType.Matrix4)
            {
                vectorLength = 4;
                return true;
            }
            else if(type == ConcreteSlotValueType.Matrix3)
            {
                vectorLength = 3;
                return true;
            }
            else if(type == ConcreteSlotValueType.Matrix2)
            {
                vectorLength = 2;
                return true;
            }
            else 
                vectorLength = 1;
                return false;
        }*/

        /*public MultiplyNode()
        {
            name = "Multiply";
        }

        protected override MethodInfo GetFunctionToConvert()
        {
            return GetType().GetMethod("Unity_Multiply", BindingFlags.Static | BindingFlags.NonPublic);
        }

        static string Unity_Multiply(
            [Slot(0, Binding.None, 0, 0, 0, 0)] DynamicDimensionVector A,
            [Slot(1, Binding.None, 2, 2, 2, 2)] DynamicDimensionVector B,
            [Slot(2, Binding.None)] out DynamicDimensionVector Out)
        {
            return
                @"
{
    Out = A * B;
}
";
        }*/
    }

    
}
