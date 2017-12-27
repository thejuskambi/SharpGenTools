﻿using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using SharpGen.Model;
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using System.Linq;

namespace SharpGen.Generator
{
    class NativeStructCodeGenerator : IMultiCodeGenerator<CsStruct, MemberDeclarationSyntax>
    {
        public NativeStructCodeGenerator(GlobalNamespaceProvider globalNamespace)
        {
            this.globalNamespace = globalNamespace;
        }

        readonly GlobalNamespaceProvider globalNamespace;

        public IEnumerable<MemberDeclarationSyntax> GenerateCode(CsStruct csElement)
        {
            var layoutKind = csElement.ExplicitLayout ? "Explicit" : "Sequential";
            var structLayoutAttribute = Attribute(ParseName("System.Runtime.InteropServices.StructLayoutAttribute"))
               .WithArgumentList(
                   AttributeArgumentList(SeparatedList(
                       new[] {
                                AttributeArgument(ParseName($"System.Runtime.InteropServices.LayoutKind.{layoutKind}")),
                                AttributeArgument(
                                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(csElement.Align)))
                                    .WithNameEquals(NameEquals(IdentifierName("Pack")))
                       }
                   )
               )
           );
            return GenerateMarshallingStructAndConversions(csElement, AttributeList(SingletonSeparatedList(structLayoutAttribute)));
        }
        private IEnumerable<MemberDeclarationSyntax> GenerateMarshallingStructAndConversions(CsStruct csStruct, AttributeListSyntax structLayoutAttributeList)
        {
            if (!csStruct.HasMarshalType || csStruct.HasCustomMarshal)
            {
                yield break;
            }

            var marshalStruct = StructDeclaration("__Native")
                .WithModifiers(TokenList(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.PartialKeyword)))
                .WithAttributeLists(SingletonList(structLayoutAttributeList))
                .WithMembers(List(csStruct.Fields.SelectMany(csField => GenerateMarshalStructField(csStruct, csField))))
                .AddMembers(GenerateMarshalFree(csStruct));
            yield return marshalStruct;

            yield return MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "__MarshalFree")
                .WithParameterList(ParameterList(SingletonSeparatedList(
                    Parameter(Identifier("@ref")).WithType(RefType(ParseTypeName("__Native"))))))
                .WithExpressionBody(ArrowExpressionClause(InvocationExpression(
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("@ref"), IdentifierName("__MarshalFree")))))
                .WithModifiers(TokenList(Token(SyntaxKind.InternalKeyword)));

            yield return GenerateMarshalFrom(csStruct);

            if (csStruct.IsOut)
            {
                yield return GenerateMarshalTo(csStruct);
            }
        }


        private IEnumerable<MemberDeclarationSyntax> GenerateMarshalStructField(CsStruct csStruct, CsField field)
        {
            var fieldDecl = FieldDeclaration(
                    VariableDeclaration(
                        ParseTypeName(field.MarshalType.QualifiedName)))
                        .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)));
            if (csStruct.ExplicitLayout)
            {
                fieldDecl = fieldDecl.WithAttributeLists(SingletonList(
                    AttributeList(
                        SingletonSeparatedList(Attribute(
                            ParseName("System.Runtime.InteropServices.FieldOffset"),
                            AttributeArgumentList(
                                SingletonSeparatedList(AttributeArgument(
                                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(field.Offset))))))))
                ));
            }
            if (field.IsArray)
            {
                yield return fieldDecl.WithDeclaration(fieldDecl.Declaration.AddVariables(
                    VariableDeclarator(field.Name)
                    ));
                for (int i = 1; i < field.ArrayDimensionValue; i++)
                {
                    var declaration = fieldDecl.WithDeclaration(fieldDecl.Declaration.AddVariables(VariableDeclarator($"__{field.Name}{i}")));
                    if (csStruct.ExplicitLayout)
                    {
                        var offset = field.Offset + (field.SizeOf / field.ArrayDimensionValue) * i;
                        declaration = declaration.WithAttributeLists(SingletonList(
                            AttributeList(
                                SingletonSeparatedList(Attribute(
                                    ParseName("System.Runtime.InteropServices.FieldOffset"),
                                    AttributeArgumentList(
                                        SingletonSeparatedList(AttributeArgument(
                                            LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(offset))))))))
                        ));
                    }
                    yield return declaration;
                }

            }
            else if (field.IsBoolToInt)
            {
                yield return fieldDecl.WithDeclaration(fieldDecl.Declaration.AddVariables(
                    VariableDeclarator($"_{field.Name}")
                    ));
            }
            else if (field.PublicType is CsStruct fieldType && fieldType.HasMarshalType)
            {
                yield return fieldDecl.WithDeclaration(VariableDeclaration(
                    ParseTypeName($"{field.MarshalType.QualifiedName}.__Native"), SingletonSeparatedList(
                        VariableDeclarator(field.Name))));
            }
            else
            {
                yield return fieldDecl.WithDeclaration(fieldDecl.Declaration.AddVariables(VariableDeclarator(field.Name)));
            }
        }

        private MethodDeclarationSyntax GenerateMarshalFree(CsStruct csStruct)
            => MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "__MarshalFree").WithBody(
                Block(
                    List(csStruct.Fields.Where(field => !field.IsArray).Select(field =>
                    {
                        if (field.PublicType.QualifiedName == "System.String")
                        {
                            return IfStatement(
                                BinaryExpression(SyntaxKind.NotEqualsExpression,
                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        ThisExpression(),
                                        IdentifierName(field.Name)),
                                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))),
                                ExpressionStatement(InvocationExpression(
                                    ParseExpression("System.Runtime.InteropServices.Marshal.FreeHGlobal"),
                                    ArgumentList(SingletonSeparatedList(
                                        Argument(
                                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                            ThisExpression(),
                                            IdentifierName(field.Name))))))));
                        }
                        else if (field.PublicType is CsStruct fieldStruct && fieldStruct.HasMarshalType)
                        {
                            return ExpressionStatement(
                            InvocationExpression(
                             MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                 MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                     ThisExpression(),
                                     IdentifierName(field.Name)),
                                 IdentifierName("__MarshalFree"))));
                        }
                        else
                        {
                            return (StatementSyntax)EmptyStatement();
                        }
                    }
                    ))))
            .WithModifiers(TokenList(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.UnsafeKeyword)));


        private MethodDeclarationSyntax GenerateMarshalTo(CsStruct csStruct)
        {
            return MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "__MarshalTo")
                .WithParameterList(ParameterList(SingletonSeparatedList(
                    Parameter(Identifier("@ref")).WithType(RefType(ParseTypeName("__Native"))))))
                .WithModifiers(TokenList(Token(SyntaxKind.InternalKeyword)))
                .WithBody(Block(
                    csStruct.Fields.Select(field =>
                    {
                        if (field.IsBoolToInt)
                        {
                            if (field.IsArray)
                            {
                                return FixedStatement(
                                    VariableDeclaration(
                                        PointerType(PredefinedType(Token(SyntaxKind.IntKeyword))),
                                        SingletonSeparatedList(
                                            VariableDeclarator("__from")
                                                .WithInitializer(EqualsValueClause(PrefixUnaryExpression(SyntaxKind.AddressOfExpression,
                                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                    IdentifierName("@ref"),
                                                    IdentifierName(field.Name))))))),
                                    ExpressionStatement(
                                        AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                            MemberAccessExpression(
                                                SyntaxKind.SimpleMemberAccessExpression,
                                                ThisExpression(),
                                                IdentifierName($"_{field.Name}")),
                                            InvocationExpression(
                                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                        IdentifierName(globalNamespace.Name),
                                                        IdentifierName("Utilities")),
                                                    IdentifierName("ConvertToIntArray")),
                                                ArgumentList(SeparatedList(new[]
                                                {
                                                        Argument(IdentifierName("__from")),
                                                        Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(field.ArrayDimensionValue)))
                                                })))
                                        )));
                            }
                            else
                            {
                                return ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        IdentifierName("@ref"),
                                        IdentifierName($"_{field.Name}")),
                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        ThisExpression(),
                                        IdentifierName($"_{field.Name}"))));
                            }
                        }
                        else if (field.IsArray)
                        {
                            if (field.IsFixedArrayOfStruct)
                            {
                                return GenerateArrayCopyMemory(field, copyFromNative: false);
                            }
                            else
                            {
                                if (field.MarshalType.QualifiedName == "System.Char")
                                {
                                    return GenerateCopyMemory(field, false,
                                        BinaryExpression(SyntaxKind.MultiplyExpression,
                                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                IdentifierName(field.Name),
                                                IdentifierName("Length")),
                                            LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(2))
                                        ));
                                }
                                else if (field.PublicType.QualifiedName == "System.String")
                                {
                                    return Block(
                                        LocalDeclarationStatement(
                                            VariableDeclaration(
                                                ParseTypeName("System.IntPtr"),
                                                SingletonSeparatedList(
                                                    VariableDeclarator($"{field.Name}_")
                                                        .WithInitializer(EqualsValueClause(
                                                            InvocationExpression(
                                                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                                        IdentifierName(globalNamespace.Name),
                                                                        IdentifierName("Utilities")),
                                                                    IdentifierName("StringToHGlobalAnsi")),
                                                                ArgumentList(SingletonSeparatedList(
                                                                    Argument(
                                                                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                                            ThisExpression(),
                                                                            IdentifierName(field.Name))))))
                                            ))))),
                                        FixedStatement(
                                            VariableDeclaration(PointerType(PredefinedType(Token(SyntaxKind.VoidKeyword))),
                                                SingletonSeparatedList(
                                                    VariableDeclarator("__ptr")
                                                    .WithInitializer(EqualsValueClause(
                                                        PrefixUnaryExpression(SyntaxKind.AddressOfExpression,
                                                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                                IdentifierName("@ref"),
                                                                IdentifierName(field.Name))))))),
                                            ExpressionStatement(InvocationExpression(
                                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                                        IdentifierName(globalNamespace.Name),
                                                                        IdentifierName("Utilities")),
                                                    IdentifierName("CopyMemory")),
                                                ArgumentList(
                                                    SeparatedList(
                                                        new[]
                                                        {
                                                                Argument(CastExpression(ParseTypeName("System.IntPtr"), IdentifierName("__ptr"))),
                                                                Argument(IdentifierName($"{field.Name}_")),
                                                                Argument(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                                        ThisExpression(),
                                                                        IdentifierName(field.Name)),
                                                                    IdentifierName("Length")))
                                                        }
                                                        )
                                                    )))),
                                        ExpressionStatement(InvocationExpression(
                                            ParseExpression("System.Runtime.InteropServices.Marshal.FreeHGlobal"),
                                            ArgumentList(SingletonSeparatedList(
                                                Argument(IdentifierName($"{field.Name}_"))))))
                                        );
                                }
                                else
                                {
                                    return GenerateArrayCopyMemory(field, copyFromNative: false);
                                }
                            }
                        }
                        else
                        {
                            if (field.PublicType.QualifiedName == "System.String")
                            {
                                return ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        IdentifierName("@ref"),
                                        IdentifierName(field.Name)),
                                    ConditionalExpression(
                                            BinaryExpression(SyntaxKind.EqualsExpression,
                                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                ThisExpression(),
                                                IdentifierName(field.Name)),
                                            LiteralExpression(SyntaxKind.NullLiteralExpression)
                                            ),
                                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                    IdentifierName("System"),
                                                    IdentifierName("IntPtr")),
                                                IdentifierName("Zero")),
                                            InvocationExpression(
                                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                        IdentifierName(globalNamespace.Name),
                                                        IdentifierName("Utilities")),
                                                    IdentifierName("StringToHGlobal" + (field.IsWideChar ? "Uni" : "Ansi"))),
                                                ArgumentList(SingletonSeparatedList(
                                                    Argument(
                                                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                            IdentifierName("@ref"),
                                                            IdentifierName(field.Name)))))))));
                            }
                            else if (field.PublicType is CsStruct structType && structType.HasMarshalType)
                            {
                                StatementSyntax marshalToStatement = ExpressionStatement(InvocationExpression(
                                    MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        MemberAccessExpression(
                                            SyntaxKind.SimpleMemberAccessExpression,
                                            ThisExpression(),
                                            IdentifierName(field.Name)),
                                        IdentifierName("__MarshalTo")))
                                .WithArgumentList(
                                    ArgumentList(
                                        SingletonSeparatedList(
                                            Argument(
                                                MemberAccessExpression(
                                                    SyntaxKind.SimpleMemberAccessExpression,
                                                    IdentifierName("@ref"),
                                                    IdentifierName(field.Name)))
                                            .WithRefOrOutKeyword(
                                                Token(SyntaxKind.RefKeyword))))));

                                return Block(
                                    ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                            IdentifierName("@ref"),
                                            IdentifierName(field.Name)),
                                            GetConstructorSyntax(structType)
                                        )),
                                    structType.GenerateAsClass ?
                                        IfStatement(
                                            BinaryExpression(SyntaxKind.NotEqualsExpression,
                                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                    ThisExpression(),
                                                    IdentifierName(field.Name)),
                                                LiteralExpression(SyntaxKind.NullLiteralExpression)),
                                            marshalToStatement)
                                        : marshalToStatement
                                    );
                            }
                            else
                            {
                                return ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        IdentifierName("@ref"),
                                        IdentifierName($"_{field.Name}")),
                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        ThisExpression(),
                                        IdentifierName($"_{field.Name}"))));
                            }
                        }
                    }
                    )));
        }

        private MethodDeclarationSyntax GenerateMarshalFrom(CsStruct csStruct)
        {
            return MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "__MarshalFrom")
                .WithParameterList(ParameterList(SingletonSeparatedList(
                    Parameter(Identifier("@ref")).WithType(RefType(ParseTypeName("__Native"))))))
                .WithModifiers(TokenList(Token(SyntaxKind.InternalKeyword)))
                .WithBody(Block(
                    csStruct.Fields.Select(field =>
                    {
                        if (field.IsBoolToInt)
                        {
                            if (field.IsArray)
                            {
                                return FixedStatement(
                                    VariableDeclaration(
                                        PointerType(PredefinedType(Token(SyntaxKind.IntKeyword))),
                                        SingletonSeparatedList(
                                            VariableDeclarator("__from")
                                                .WithInitializer(EqualsValueClause(PrefixUnaryExpression(SyntaxKind.AddressOfExpression,
                                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                    IdentifierName("@ref"),
                                                    IdentifierName(field.Name))))))),
                                    ExpressionStatement(
                                        AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                            MemberAccessExpression(
                                                SyntaxKind.SimpleMemberAccessExpression,
                                                ThisExpression(),
                                                IdentifierName($"_{field.Name}")),
                                            InvocationExpression(
                                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                        IdentifierName(globalNamespace.Name),
                                                        IdentifierName("Utilities")),
                                                    IdentifierName("ConvertToBoolArray")),
                                                ArgumentList(SeparatedList(new[]
                                                {
                                                        Argument(IdentifierName("__from")),
                                                        Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(field.ArrayDimensionValue)))
                                                })))
                                        )));
                            }
                            else
                            {
                                return ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        ThisExpression(),
                                        IdentifierName($"_{field.Name}")),
                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        IdentifierName("@ref"),
                                        IdentifierName($"_{field.Name}"))));
                            }
                        }
                        else if (field.IsArray)
                        {
                            if (field.IsFixedArrayOfStruct)
                            {
                                return GenerateArrayCopyMemory(field, copyFromNative: true);
                            }
                            else
                            {
                                if (field.MarshalType.QualifiedName == "System.Char")
                                {
                                    return FixedStatement(
                                        VariableDeclaration(
                                            PointerType(PredefinedType(Token(SyntaxKind.CharKeyword))),
                                            SingletonSeparatedList(
                                                VariableDeclarator("__ptr")
                                                    .WithInitializer(EqualsValueClause(PrefixUnaryExpression(SyntaxKind.AddressOfExpression,
                                                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("@ref"), IdentifierName(field.Name))))))),
                                        ExpressionStatement(
                                            AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                                MemberAccessExpression(
                                                    SyntaxKind.SimpleMemberAccessExpression,
                                                    ThisExpression(),
                                                    IdentifierName(field.Name)),
                                                InvocationExpression(
                                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                            IdentifierName(globalNamespace.Name),
                                                            IdentifierName("Utilities")),
                                                        IdentifierName("PtrToStringUni")),
                                                    ArgumentList(SeparatedList(new[]
                                                    {
                                                            Argument(
                                                                CastExpression(ParseTypeName("System.IntPtr"),
                                                                IdentifierName("__ptr"))),
                                                            Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(field.ArrayDimensionValue)))
                                                    })))
                                            )));
                                }
                                else if (field.PublicType.QualifiedName == "System.String" && field.MarshalType.QualifiedName == "System.Byte")
                                {
                                    return FixedStatement(
                                        VariableDeclaration(
                                            PointerType(PredefinedType(Token(SyntaxKind.VoidKeyword))),
                                            SingletonSeparatedList(
                                                VariableDeclarator("__ptr")
                                                    .WithInitializer(EqualsValueClause(PrefixUnaryExpression(SyntaxKind.AddressOfExpression,
                                                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("@ref"), IdentifierName(field.Name))))))),
                                        ExpressionStatement(
                                            AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                                MemberAccessExpression(
                                                    SyntaxKind.SimpleMemberAccessExpression,
                                                    ThisExpression(),
                                                    IdentifierName(field.Name)),
                                                InvocationExpression(
                                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                            IdentifierName(globalNamespace.Name),
                                                            IdentifierName("Utilities")),
                                                        IdentifierName("PtrToStringAnsi")),
                                                    ArgumentList(SeparatedList(new[]
                                                    {
                                                            Argument(
                                                                CastExpression(ParseTypeName("System.IntPtr"),
                                                                IdentifierName("__ptr"))),
                                                            Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(field.ArrayDimensionValue)))
                                                    })))
                                            )));
                                }
                                else
                                {
                                    return GenerateArrayCopyMemory(field, copyFromNative: true);
                                }
                            }
                        }
                        else
                        {
                            if (field.PublicType.QualifiedName == "System.String")
                            {
                                return ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        ThisExpression(),
                                        IdentifierName(field.Name)),
                                    ConditionalExpression(
                                            BinaryExpression(SyntaxKind.EqualsExpression,
                                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                IdentifierName("@ref"),
                                                IdentifierName(field.Name)),
                                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                    IdentifierName("System"),
                                                    IdentifierName("IntPtr")),
                                                IdentifierName("Zero"))),
                                            LiteralExpression(SyntaxKind.NullLiteralExpression),
                                            InvocationExpression(
                                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                    ParseTypeName("System.Runtime.InteropServices.Marshal"),
                                                    IdentifierName("PtrToString" + (field.IsWideChar ? "Uni" : "Ansi"))),
                                                ArgumentList(SingletonSeparatedList(
                                                    Argument(
                                                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                            IdentifierName("@ref"),
                                                            IdentifierName(field.Name)))))))));
                            }
                            else if (field.PublicType is CsStruct structType && structType.HasMarshalType)
                            {
                                return Block(
                                        ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                ThisExpression(),
                                                IdentifierName(field.Name)),
                                            ObjectCreationExpression(ParseTypeName(field.PublicType.QualifiedName))
                                        )),
                                        ExpressionStatement(InvocationExpression(
                                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                    ThisExpression(),
                                                    IdentifierName(field.Name)),
                                                IdentifierName("__MarshalFrom")),
                                            ArgumentList(SingletonSeparatedList(
                                                    Argument(
                                                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                                            IdentifierName("@ref"),
                                                            IdentifierName(field.Name)))
                                                        .WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)))))));
                            }
                            else
                            {
                                return ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        ThisExpression(),
                                        IdentifierName(field.Name)),
                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        IdentifierName("@ref"),
                                        IdentifierName(field.Name))));
                            }
                        }
                    }
                    )));
        }
        
        private StatementSyntax GenerateArrayCopyMemory(CsField field, bool copyFromNative)
            => GenerateCopyMemory(
                field,
                copyFromNative,
                BinaryExpression(SyntaxKind.MultiplyExpression,
                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(field.ArrayDimensionValue)),
                    SizeOfExpression(ParseTypeName(field.MarshalType.QualifiedName))
                ));

        private StatementSyntax GenerateCopyMemory(CsField field, bool copyFromNative, ExpressionSyntax numBytesExpression)
        {
            return FixedStatement(
                VariableDeclaration(
                    PointerType(PredefinedType(Token(SyntaxKind.VoidKeyword))),
                    SeparatedList(
                        new[]
                        {
                            VariableDeclarator(copyFromNative ? "__to": "__from")
                                .WithInitializer(EqualsValueClause(
                                    PrefixUnaryExpression(
                                        SyntaxKind.AddressOfExpression,
                                        ElementAccessExpression(
                                            MemberAccessExpression(
                                                SyntaxKind.SimpleMemberAccessExpression,
                                                ThisExpression(),
                                                IdentifierName(field.Name)))
                                        .WithArgumentList(
                                            BracketedArgumentList(
                                                SingletonSeparatedList(
                                                    Argument(
                                                        LiteralExpression(
                                                            SyntaxKind.NumericLiteralExpression,
                                                            Literal(0))))))))),
                            VariableDeclarator(copyFromNative ? "__from" : "__to")
                                .WithInitializer(EqualsValueClause(
                                    PrefixUnaryExpression(SyntaxKind.AddressOfExpression,
                                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                    IdentifierName("@ref"),
                                    IdentifierName(field.Name)))))
                        })
                    ),
                ExpressionStatement(
                    InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                IdentifierName(globalNamespace.Name),
                                IdentifierName("Utilities")),
                            IdentifierName("CopyMemory")),
                        ArgumentList(
                            SeparatedList(
                                new[]
                                {
                                                            Argument(CastExpression(ParseTypeName("System.IntPtr"), IdentifierName("__to"))),
                                                            Argument(CastExpression(ParseTypeName("System.IntPtr"), IdentifierName("__from"))),
                                                            Argument(numBytesExpression)
                                }
                            )))));
        }


        private static ExpressionSyntax GetConstructorSyntax(CsStruct structType)
        {
            if (structType.HasCustomNew)
            {
                return InvocationExpression(ParseExpression($"{structType.QualifiedName}.__NewNative"));
            }
            else
            {
                return ObjectCreationExpression(ParseTypeName($"{structType.QualifiedName}.__Native"));
            }
        }


    }
}
