grammar Bond;

// ===== Parser Rules =====

bond
    : whiteSpace? import_* namespace+ declaration* EOF
    ;

import_
    : IMPORT STRING_LITERAL SEMI?
    ;

namespace
    : NAMESPACE language? qualifiedName SEMI?
    ;

language
    : CPP | CSHARP | CS | JAVA
    ;

identifier
    : IDENTIFIER
    | STREAM
    | VALUE
    ;

qualifiedName
    : identifier (DOT identifier)*
    ;

declaration
    : forward
    | alias
    | structDecl
    | enum
    | service
    ;

forward
    : STRUCT identifier typeParameters? SEMI
    ;

alias
    : USING identifier typeParameters? EQUAL type SEMI
    ;

structDecl
    : attributes? STRUCT identifier typeParameters? (structView | structDef)
    ;

structView
    : VIEW_OF qualifiedName LBRACE viewFieldList RBRACE SEMI?
    ;

viewFieldList
    : identifier (semiOrComma identifier)* semiOrComma?
    ;

structDef
    : (COLON userType)? LBRACE (field SEMI)* RBRACE SEMI?
    ;

enum
    : attributes? ENUM identifier LBRACE enumConstant (semiOrComma enumConstant)* semiOrComma? RBRACE SEMI?
    ;

enumConstant
    : identifier (EQUAL (MINUS | PLUS)? INTEGER_LITERAL)?
    ;

service
    : attributes? SERVICE identifier typeParameters? (COLON serviceType)? LBRACE method* RBRACE SEMI?
    ;

method
    : attributes? (methodResultType identifier LPAREN methodParameter? RPAREN | NOTHING identifier LPAREN methodParameter? RPAREN) SEMI?
    ;

methodParameter
    : methodInputType identifier?
    ;

methodResultType
    : VOID
    | methodTypeStreaming
    | methodTypeUnary
    ;

methodInputType
    : VOID
    | methodTypeStreaming
    | methodTypeUnary
    ;

methodTypeStreaming
    : STREAM userStructRef
    ;

methodTypeUnary
    : userStructRef
    ;

field
    : attributes? fieldOrdinal COLON modifier? fieldType fieldIdentifier (EQUAL default_)?
    ;

fieldIdentifier
    : identifier
    ;

fieldOrdinal
    : INTEGER_LITERAL
    ;

modifier
    : OPTIONAL
    | REQUIRED_OPTIONAL
    | REQUIRED
    ;

fieldType
    : BOND_META_NAME
    | BOND_META_FULL_NAME
    | type
    ;

type
    : basicType
    | complexType
    | userType
    ;

basicType
    : INT8
    | INT16
    | INT32
    | INT64
    | UINT8
    | UINT16
    | UINT32
    | UINT64
    | FLOAT
    | DOUBLE
    | STRING
    | WSTRING
    | BOOL
    ;

complexType
    : LIST LANGLE type RANGLE
    | BLOB
    | VECTOR LANGLE type RANGLE
    | NULLABLE LANGLE type RANGLE
    | SET LANGLE keyType RANGLE
    | MAP LANGLE keyType COMMA type RANGLE
    | BONDED LANGLE userStructRef RANGLE
    ;

keyType
    : basicType
    | userType
    ;

userType
    : qualifiedName typeArgs?
    ;

userStructRef
    : qualifiedName typeArgs?
    ;

serviceType
    : qualifiedName typeArgs?
    ;

typeArgs
    : LANGLE typeArg (COMMA typeArg)* RANGLE
    ;

typeArg
    : type
    | (MINUS | PLUS)? INTEGER_LITERAL
    ;

typeParameters
    : LANGLE typeParam (COMMA typeParam)* RANGLE
    ;

typeParam
    : identifier constraint?
    ;

constraint
    : COLON VALUE
    ;

default_
    : TRUE
    | FALSE
    | NOTHING
    | L? STRING_LITERAL
    | identifier
    | (MINUS | PLUS)? FLOAT_LITERAL
    | (MINUS | PLUS)? INTEGER_LITERAL
    ;

attributes
    : attribute+
    ;

attribute
    : LBRACKET qualifiedName LPAREN STRING_LITERAL RPAREN RBRACKET
    ;

semiOrComma
    : SEMI | COMMA
    ;

whiteSpace
    : (WS | COMMENT | LINE_COMMENT)*
    ;

// ===== Lexer Rules =====

// Keywords
IMPORT : 'import';
NAMESPACE : 'namespace';
USING : 'using';
STRUCT : 'struct';
ENUM : 'enum';
SERVICE : 'service';
VIEW_OF : 'view_of';

// Language keywords
CPP : 'cpp';
CSHARP : 'csharp';
CS : 'cs';
JAVA : 'java';

// Type keywords
INT8 : 'int8';
INT16 : 'int16';
INT32 : 'int32';
INT64 : 'int64';
UINT8 : 'uint8';
UINT16 : 'uint16';
UINT32 : 'uint32';
UINT64 : 'uint64';
FLOAT : 'float';
DOUBLE : 'double';
STRING : 'string';
WSTRING : 'wstring';
BOOL : 'bool';
BLOB : 'blob';
LIST : 'list';
VECTOR : 'vector';
NULLABLE : 'nullable';
SET : 'set';
MAP : 'map';
BONDED : 'bonded';

// Modifiers
OPTIONAL : 'optional';
REQUIRED : 'required';
REQUIRED_OPTIONAL : 'required_optional';

// Method types
VOID : 'void';
STREAM : 'stream';
NOTHING : 'nothing';

// Constraint keyword
VALUE : 'value';

// Meta types
BOND_META_NAME : 'bond_meta::name';
BOND_META_FULL_NAME : 'bond_meta::full_name';

// Boolean literals
TRUE : 'true';
FALSE : 'false';

// Operators and punctuation
LBRACE : '{';
RBRACE : '}';
LBRACKET : '[';
RBRACKET : ']';
LPAREN : '(';
RPAREN : ')';
LANGLE : '<';
RANGLE : '>';
SEMI : ';';
COLON : ':';
COMMA : ',';
DOT : '.';
EQUAL : '=';
MINUS : '-';
PLUS : '+';
L : 'L';

// Identifiers
IDENTIFIER
    : [\p{L}_][\p{L}\p{N}_]*
    ;

// Literals
INTEGER_LITERAL
    : [0-9]+
    | '0' [xX] [0-9a-fA-F]+
    | '0' [oO] [0-7]+
    ;

FLOAT_LITERAL
    : [0-9]+ '.' [0-9]+ ([eE] [+-]? [0-9]+)?
    | [0-9]+ [eE] [+-]? [0-9]+
    ;

STRING_LITERAL
    : '"' ( ESC | ~[\\"\r\n] )* '"'
    ;

fragment ESC
    : '\\' [btnfr"'\\]
    | '\\' 'x' [0-9a-fA-F] [0-9a-fA-F]
    | '\\' [0-7] [0-7]? [0-7]?
    | '\\' 'u' [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]
    | '\\' 'U' [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]
    | '\\' ~[\r\n]  // Allow any character except newlines after backslash
    ;

// Comments
COMMENT
    : '/*' .*? '*/' -> channel(HIDDEN)
    ;

LINE_COMMENT
    : '//' ~[\r\n]* -> channel(HIDDEN)
    ;

// Whitespace
WS
    : [ \t\r\n\f\u000B\u0085\p{Zs}\p{Zl}\p{Zp}]+ -> channel(HIDDEN)
    ;
